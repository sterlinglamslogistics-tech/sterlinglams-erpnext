using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using System.Text.RegularExpressions;

namespace SterlingLams.Web.Services;

public interface IWooCommerceImportService
{
    Task<WooImportResult> ImportFromCsvAsync(Stream csvStream);
}

public class WooImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Success => !Errors.Any();
    public string Summary =>
        $"{Created} created, {Updated} updated, {Skipped} skipped" +
        (Errors.Any() ? $", {Errors.Count} errors" : "");
}

public class WooCommerceImportService : IWooCommerceImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WooCommerceImportService> _logger;

    // WooCommerce category keyword → local category slug (first match wins)
    private static readonly (string Fragment, string Slug)[] CategoryMap =
    {
        ("Earrings",         "earrings"),
        ("Necklaces",        "necklaces"),
        ("Pendants",         "necklaces"),
        ("Rings",            "rings"),
        ("Bracelets",        "bracelets"),
        ("Bangles",          "bracelets"),
        ("Sets",             "sets"),
        ("Clutches",         "clutches"),
        ("Sunglasses",       "accessories"),
        ("Brooches",         "accessories"),
        ("Waist Chains",     "accessories"),
        ("Hair Accessories", "accessories"),
        ("Cufflinks",        "accessories"),
        ("Scarfs",           "accessories"),
        ("Caps",             "accessories"),
        ("Watches",          "watches"),
        ("Mens",             "accessories"),
    };

    public WooCommerceImportService(ApplicationDbContext db, ILogger<WooCommerceImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<WooImportResult> ImportFromCsvAsync(Stream csvStream)
    {
        var result = new WooImportResult();

        // Clear all existing products before importing
        var toDelete = await _db.Products.Include(p => p.Images).ToListAsync();
        _db.Products.RemoveRange(toDelete);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Cleared {Count} existing products before WooCommerce import.", toDelete.Count);

        var categories = await _db.Categories.ToListAsync();
        var existingByCode = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);

        var rows = ParseCsv(csvStream);
        var published = rows.Where(r => Get(r, "post_status") == "Published").ToList();

        int batchCount = 0;

        foreach (var row in published)
        {
            try
            {
                var name = Get(row, "post_title");
                if (string.IsNullOrWhiteSpace(name)) { result.Skipped++; continue; }

                var priceStr = Get(row, "regular_price");
                if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0)
                {
                    result.Skipped++;
                    continue;
                }

                var sku  = Get(row, "sku");
                var id   = Get(row, "ID");
                var code = "WC-" + (string.IsNullOrWhiteSpace(sku) ? id : sku);

                var category = ResolveCategory(Get(row, "tax:product_cat"), categories)
                            ?? await EnsureCategoryAsync("Accessories", "accessories", categories);

                var description = StripHtml(Get(row, "post_content"));
                var shortDesc   = StripHtml(Get(row, "post_excerpt"));
                var colour      = Get(row, "attribute:Colour");
                var imageUrls   = ParseImageUrls(Get(row, "images"));

                if (existingByCode.TryGetValue(code, out var existing))
                {
                    existing.Name             = name;
                    existing.Price            = price;
                    existing.CategoryId       = category.Id;
                    existing.UpdatedAt        = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(description))   existing.Description      = description;
                    if (!string.IsNullOrWhiteSpace(shortDesc))     existing.ShortDescription = shortDesc;
                    if (!string.IsNullOrWhiteSpace(colour))        existing.Metal            = colour;

                    var existingUrls = existing.Images.Select(i => i.Url).ToHashSet();
                    var nextSort     = existing.Images.Any() ? existing.Images.Max(i => i.SortOrder) : 0;
                    foreach (var url in imageUrls.Where(u => !existingUrls.Contains(u)))
                    {
                        nextSort++;
                        existing.Images.Add(new ProductImage { Url = url, IsPrimary = nextSort == 1 && !existingUrls.Any(), SortOrder = nextSort });
                    }

                    result.Updated++;
                }
                else
                {
                    var slug    = await UniqueSlugAsync(Slugify(name));
                    var product = new Product
                    {
                        ErpNextItemCode  = code,
                        Sku              = sku,
                        Name             = name,
                        Slug             = slug,
                        Price            = price,
                        Description      = description,
                        ShortDescription = shortDesc,
                        Metal            = colour,
                        IsActive         = true,
                        CategoryId       = category.Id,
                        CreatedAt        = DateTime.UtcNow,
                        UpdatedAt        = DateTime.UtcNow,
                    };

                    var sort = 0;
                    foreach (var url in imageUrls)
                    {
                        sort++;
                        product.Images.Add(new ProductImage { Url = url, IsPrimary = sort == 1, SortOrder = sort });
                    }

                    _db.Products.Add(product);
                    existingByCode[code] = product;
                    result.Created++;
                }

                batchCount++;
                if (batchCount % 50 == 0)
                    await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                var msg = $"Error importing '{Get(row, "post_title")}': {ex.Message}";
                _logger.LogWarning(ex, msg);
                result.Errors.Add(msg);
                result.Skipped++;
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("WooCommerce CSV import complete: {Summary}", result.Summary);
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> ParseCsv(Stream stream)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");

        if (parser.EndOfData) return rows;

        var rawHeaders = parser.ReadFields() ?? Array.Empty<string>();
        var headers    = DeduplicateHeaders(rawHeaders);

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            var row    = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < headers.Length && i < fields.Length; i++)
                row[headers[i]] = fields[i];
            rows.Add(row);
        }
        return rows;
    }

    private static string[] DeduplicateHeaders(string[] raw)
    {
        var result = new string[raw.Length];
        var seen   = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Length; i++)
        {
            var h = raw[i];
            if (seen.TryGetValue(h, out var n)) { seen[h]++; result[i] = $"{h}_{n}"; }
            else                                { seen[h] = 1;    result[i] = h; }
        }
        return result;
    }

    private Category? ResolveCategory(string? wooCategory, List<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(wooCategory)) return null;
        foreach (var (fragment, slug) in CategoryMap)
        {
            if (wooCategory.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return categories.FirstOrDefault(c => c.Slug == slug);
        }
        return null;
    }

    private async Task<Category> EnsureCategoryAsync(string name, string slug, List<Category> categories)
    {
        var cat = categories.FirstOrDefault(c => c.Slug == slug);
        if (cat != null) return cat;
        cat = new Category { Name = name, Slug = slug, IsActive = true };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        categories.Add(cat);
        return cat;
    }

    private static List<string> ParseImageUrls(string? imageField)
    {
        if (string.IsNullOrWhiteSpace(imageField)) return new();
        return Regex.Matches(imageField, @"https?://[^\s,!""]+")
                    .Select(m => m.Value.TrimEnd(',', ' '))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .ToList();
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return Regex.Replace(html, "<[^>]*>", " ")
                    .Replace("&nbsp;", " ").Replace("&amp;", "&")
                    .Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Trim();
    }

    private async Task<string> UniqueSlugAsync(string baseSlug)
    {
        var slug = baseSlug; var n = 1;
        while (await _db.Products.AnyAsync(p => p.Slug == slug))
            slug = $"{baseSlug}-{n++}";
        return slug;
    }

    private static string Slugify(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

    private static string Get(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
}
