using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class ReportsController : AdminBaseController
{
    protected override string Section => "Reports";

    private readonly ApplicationDbContext _db;
    public ReportsController(ApplicationDbContext db) => _db = db;

    public IActionResult Index() => RedirectToAction(nameof(Sales));

    // Resolve the from/to range (inclusive days). Defaults to the last 30 days.
    private static (DateTime From, DateTime ToExclusive) Range(string? from, string? to)
    {
        var today = DateTime.UtcNow.Date;
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : today.AddDays(-29);
        var t = DateTime.TryParse(to, out var pt) ? pt.Date : today;
        if (t < f) t = f;
        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Utc));
    }

    public record KV(string Label, decimal Amount);
    public record DayRow(DateTime Day, int Count, decimal Total);
    public record BranchRow(string Label, int Count, decimal Total);

    public class SalesVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public int Count { get; set; }
        public decimal Gross { get; set; }
        public decimal Refunds { get; set; }
        public decimal Net => Gross - Refunds;
        public decimal Avg => Count > 0 ? Gross / Count : 0;
        public List<KV> ByPayment { get; set; } = new();
        public List<DayRow> ByDay { get; set; } = new();
        public List<BranchRow> ByBranch { get; set; } = new();
    }

    public async Task<IActionResult> Sales(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Sales Report";
        var (f, t) = Range(from, to);

        var orders = _db.Orders.Where(o => o.IsPaid && o.CreatedAt >= f && o.CreatedAt < t);
        if (storeId.HasValue) orders = orders.Where(o => o.PickupStoreId == storeId);

        var list = await orders.Select(o => new { o.Total, o.PaymentProvider, o.Channel, o.CreatedAt, o.PickupStoreId }).ToListAsync();
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var storeName = stores.ToDictionary(s => s.Id, s => s.Name);

        var refunds = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refunds = refunds.Where(r => r.OriginalOrder.PickupStoreId == storeId);
        var refundTotal = await refunds.SumAsync(r => (decimal?)r.Amount) ?? 0;

        var vm = new SalesVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            Count = list.Count,
            Gross = list.Sum(x => x.Total),
            Refunds = refundTotal,
            ByPayment = list.GroupBy(x => string.IsNullOrEmpty(x.PaymentProvider) ? "Other" : x.PaymentProvider)
                            .Select(g => new KV(g.Key, g.Sum(x => x.Total)))
                            .OrderByDescending(k => k.Amount).ToList(),
            ByDay = list.GroupBy(x => x.CreatedAt.Date)
                        .Select(g => new DayRow(g.Key, g.Count(), g.Sum(x => x.Total)))
                        .OrderByDescending(d => d.Day).ToList(),
            ByBranch = list.GroupBy(x => x.PickupStoreId)
                           .Select(g => new BranchRow(
                               g.Key.HasValue && storeName.ContainsKey(g.Key.Value) ? storeName[g.Key.Value] : "Online / unassigned",
                               g.Count(), g.Sum(x => x.Total)))
                           .OrderByDescending(b => b.Total).ToList()
        };
        return View(vm);
    }

    public record ProductRow(string Name, string? Sku, int Units, decimal Revenue);

    public async Task<IActionResult> Products(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Best Sellers";
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t.AddDays(-1); ViewBag.StoreId = storeId;
        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var q = _db.OrderItems.Where(i => i.Order.IsPaid && i.Order.CreatedAt >= f && i.Order.CreatedAt < t);
        if (storeId.HasValue) q = q.Where(i => i.Order.PickupStoreId == storeId);

        var grouped = await q.GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Quantity * x.UnitPrice)
            })
            .OrderByDescending(r => r.Revenue)
            .Take(100)
            .ToListAsync();

        var ids = grouped.Select(g => g.ProductId).ToList();
        var skus = await _db.Products.Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sku);

        var rows = grouped
            .Select(g => new ProductRow(g.ProductName, skus.GetValueOrDefault(g.ProductId), g.Units, g.Revenue))
            .ToList();
        return View(rows);
    }

    public class StockVm
    {
        public decimal TotalValue { get; set; }
        public int TotalUnits { get; set; }
        public List<BranchRow> ByBranch { get; set; } = new();
        public List<LowStockRow> LowStock { get; set; } = new();
        public int OutOfStock { get; set; }
    }
    public record LowStockRow(string Product, string Store, int Qty, int Threshold);

    public async Task<IActionResult> Stock()
    {
        ViewData["Title"] = "Stock Report";

        var inv = await _db.StoreInventories
            .Select(si => new
            {
                si.StoreId,
                StoreName = si.Store.Name,
                si.QuantityOnHand,
                Price = si.Product.Price,
                Product = si.Product.Name,
                si.Product.LowStockThreshold,
                Active = si.Product.IsActive
            })
            .Where(x => x.Active)
            .ToListAsync();

        var vm = new StockVm
        {
            TotalUnits = inv.Sum(x => x.QuantityOnHand),
            TotalValue = inv.Sum(x => x.QuantityOnHand * x.Price),
            OutOfStock = inv.Count(x => x.QuantityOnHand <= 0),
            ByBranch = inv.GroupBy(x => x.StoreName)
                          .Select(g => new BranchRow(g.Key, g.Sum(x => x.QuantityOnHand), g.Sum(x => x.QuantityOnHand * x.Price)))
                          .OrderByDescending(b => b.Total).ToList(),
            LowStock = inv.Where(x => x.QuantityOnHand > 0 && x.QuantityOnHand <= x.LowStockThreshold)
                          .OrderBy(x => x.QuantityOnHand)
                          .Select(x => new LowStockRow(x.Product, x.StoreName, x.QuantityOnHand, x.LowStockThreshold))
                          .Take(100).ToList()
        };
        return View(vm);
    }
}
