using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class AuditLogsController : BaseController
    {
        public AuditLogsController(ILogger<AuditLogsController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
        }

        // GET: Admin/Audits/Index
        public async Task<IActionResult> Index(string search, int page = 1, int pageSize = 10)
        {
            IQueryable<AuditLog> query = _dbContext.AuditLogs.AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(log =>
                    log.UserId.Contains(search) ||
                    log.Action.Contains(search) ||
                    log.Details.Contains(search));
            }

            int totalCount = await query.CountAsync();
            ViewBag.TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            ViewBag.CurrentPage = page;
            ViewBag.Search = search;
            
            List<AuditLog> auditLogs = await query
                .OrderByDescending(log => log.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            if (!auditLogs.Any())
            {
                ViewBag.Message = "No audit logs found.";
            }

            SetPageTitle("Audit Logs");
            return View(auditLogs);
        }

    }
}
