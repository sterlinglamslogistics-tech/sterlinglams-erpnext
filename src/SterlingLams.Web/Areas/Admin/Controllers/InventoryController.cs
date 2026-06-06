using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class InventoryController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryService _inventory;
        private const int PageSize = 30;

        public InventoryController(ApplicationDbContext db, IInventoryService inventory)
        {
            _db = db;
            _inventory = inventory;
        }

        // ── Index: product-centric view (all stores as columns) ───────────────────
        public async Task<IActionResult> Index(string q = "", int page = 1)
        {
            ViewData["Title"] = "Inventory";

            var stores = await _db.Stores
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();

            // Build query over products
            var productQuery = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)
                .Where(p => p.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                productQuery = productQuery.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            var total    = await productQuery.CountAsync();
            var products = await productQuery
                .OrderBy(p => p.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            // Load all StoreInventory records for this page of products in one query
            var productIds = products.Select(p => p.Id).ToList();
            var allInventory = await _db.StoreInventories
                .Where(si => productIds.Contains(si.ProductId))
                .ToListAsync();

            var rows = products.Select(p =>
            {
                var stockByStore = stores.ToDictionary(
                    s => s.Id,
                    s => allInventory.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id)
                                    ?.QuantityOnHand ?? -1   // -1 = no record exists yet
                );

                return new ProductInventoryRow
                {
                    ProductId        = p.Id,
                    ProductName      = p.Name,
                    Sku              = p.Sku,
                    CategoryName     = p.Category?.Name ?? "—",
                    ImageUrl         = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
                    LowStockThreshold = p.LowStockThreshold,
                    StockByStore     = stockByStore,
                };
            }).ToList();

            var lastSync = await _db.StoreInventories
                .MaxAsync(si => (DateTime?)si.LastSyncedAt);

            return View(new AdminInventoryViewModel
            {
                Stores       = stores,
                Products     = rows,
                LastSyncedAt = lastSync,
                SearchQuery  = q,
                CurrentPage  = page,
                TotalPages   = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount   = total,
            });
        }

        // ── Set stock for a product across all stores (saves absolute quantities) ─
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetProductStock(int productId, IFormCollection form)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();

            foreach (var store in stores)
            {
                var key = $"store_{store.Id}";
                if (!form.TryGetValue(key, out var qtyStr)) continue;
                if (!int.TryParse(qtyStr, out var qty) || qty < 0) continue;

                var existing = await _db.StoreInventories
                    .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == store.Id);

                if (existing != null)
                {
                    existing.QuantityOnHand = qty;
                    existing.LastSyncedAt   = DateTime.UtcNow;
                }
                else
                {
                    _db.StoreInventories.Add(new StoreInventory
                    {
                        ProductId      = productId,
                        StoreId        = store.Id,
                        QuantityOnHand = qty,
                        LastSyncedAt   = DateTime.UtcNow,
                    });
                }
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Stock updated for '{product.Name}'.";

            var q    = form["q"].ToString();
            var page = int.TryParse(form["page"], out var p) ? p : 1;
            return RedirectToAction(nameof(Index), new { q, page });
        }

        // ── Ensure all product × store combinations have an inventory record ──────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EnsureInventoryRecords()
        {
            var products = await _db.Products.Where(p => p.IsActive).Select(p => p.Id).ToListAsync();
            var stores   = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();

            var existing = await _db.StoreInventories
                .Select(si => new { si.ProductId, si.StoreId })
                .ToListAsync();

            var existingSet = existing.Select(e => (e.ProductId, e.StoreId)).ToHashSet();
            int created = 0;

            foreach (var productId in products)
            {
                foreach (var storeId in stores)
                {
                    if (!existingSet.Contains((productId, storeId)))
                    {
                        _db.StoreInventories.Add(new StoreInventory
                        {
                            ProductId      = productId,
                            StoreId        = storeId,
                            QuantityOnHand = 0,
                            LastSyncedAt   = DateTime.UtcNow,
                        });
                        created++;
                    }
                }
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Created {created} missing inventory record(s). All products now appear in the grid.";
            return RedirectToAction(nameof(Index));
        }

        // ── Sync from ERPNext ─────────────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Sync()
        {
            try
            {
                await _inventory.SyncAllAsync();
                TempData["Success"] = "Inventory synced from ERPNext.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Sync failed: {ex.Message}";
            }
            return RedirectToAction(nameof(Index));
        }

        // ── Update low-stock threshold for a product ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateThreshold(int productId, int threshold)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            product.LowStockThreshold = Math.Max(0, threshold);
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Threshold for '{product.Name}' set to {threshold}.";
            return RedirectToAction(nameof(Index));
        }
    }
}
