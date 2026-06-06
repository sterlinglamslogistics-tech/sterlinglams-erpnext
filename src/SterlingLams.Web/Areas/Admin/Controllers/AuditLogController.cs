using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class AuditLogController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private const int PageSize = 50;

        public AuditLogController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(int page = 1, string action = "", string entity = "", string dateFrom = "", string dateTo = "")
        {
            ViewData["Title"] = "Audit Log";

            var query = _db.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(l => l.Action == action);

            if (!string.IsNullOrWhiteSpace(entity))
                query = query.Where(l => l.EntityType == entity);

            if (DateTime.TryParse(dateFrom, out var from))
                query = query.Where(l => l.CreatedAt >= from);

            if (DateTime.TryParse(dateTo, out var to))
                query = query.Where(l => l.CreatedAt < to.AddDays(1));

            var total = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(l => new AuditLogRow
                {
                    Action = l.Action, EntityType = l.EntityType, EntityId = l.EntityId,
                    Description = l.Description, PerformedBy = l.PerformedBy, CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            var availableActions  = await _db.AuditLogs.Select(l => l.Action).Distinct().OrderBy(a => a).ToListAsync();
            var availableEntities = await _db.AuditLogs.Select(l => l.EntityType).Distinct().OrderBy(e => e).ToListAsync();

            return View(new AdminAuditLogViewModel
            {
                Logs = logs,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                ActionFilter = action, EntityFilter = entity, DateFrom = dateFrom, DateTo = dateTo,
                AvailableActions = availableActions, AvailableEntities = availableEntities
            });
        }
    }
}
