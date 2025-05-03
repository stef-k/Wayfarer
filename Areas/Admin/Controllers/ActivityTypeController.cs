using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class ActivityTypeController : BaseController
    {
        public ActivityTypeController(ILogger<ActivityTypeController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        { }

        // GET: Admin/ActivityType
        public async Task<IActionResult> Index(int page = 1, string search = "")
        {
            // Define the number of items per page
            int pageSize = 15;

            // Start with all ActivityTypes, and apply search filter if provided
            IQueryable<ActivityType> activityTypesQuery = _dbContext.ActivityTypes
                .Where(a => string.IsNullOrEmpty(search) || a.Name.Contains(search));

            // Get the total count of ActivityTypes after applying the search filter
            int totalItems = await activityTypesQuery.CountAsync();

            // Apply pagination to the query
            List<ActivityType> activityTypes = await activityTypesQuery
                .Skip((page - 1) * pageSize)  // Skip the previous pages
                .Take(pageSize)               // Take only the items for the current page
                .ToListAsync();

            // Calculate total pages
            int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

            // Pass data to the view via ViewBag
            ViewBag.Search = search;
            ViewBag.TotalPages = totalPages;
            ViewBag.CurrentPage = page;

            // Return the view with the paginated list of ActivityTypes
            SetPageTitle("Available Activities");
            return View(activityTypes);
        }


        // GET: Admin/ActivityType/Create
        public async Task<IActionResult> Create()
        {
            SetPageTitle("New Activity Type");
            return View();
        }

        // POST: Admin/ActivityType/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,Description")] ActivityType activityType)
        {
            if (!ValidateModelState())
            {
                return View(activityType);
            }

            try
            {
                _dbContext.ActivityTypes.Add(activityType);
                await _dbContext.SaveChangesAsync();

                SetAlert("Activity Type created successfully.", "success");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View(activityType);
            }
        }

        // GET: Admin/ActivityType/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            ActivityType? activityType = await _dbContext.ActivityTypes.FindAsync(id);
            return activityType == null ? (IActionResult)NotFound() : View(activityType);
        }

        // POST: Admin/ActivityType/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,Description")] ActivityType activityType)
        {
            if (id != activityType.Id)
            {
                return NotFound();
            }

            if (!ValidateModelState())
            {
                return View(activityType);
            }

            try
            {
                _dbContext.Update(activityType);
                await _dbContext.SaveChangesAsync();

                SetAlert("Activity Type updated successfully.", "success");
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateConcurrencyException ex)
            {
                HandleError(ex);
                if (!ActivityTypeExists(activityType.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
        }

        // GET: Admin/ActivityType/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            ActivityType? activityType = await _dbContext.ActivityTypes
                .FirstOrDefaultAsync(m => m.Id == id);
            return activityType == null ? (IActionResult)NotFound() : View(activityType);
        }

        // POST: Admin/ActivityType/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            ActivityType? activityType = await _dbContext.ActivityTypes.FindAsync(id);
            if (activityType != null)
            {
                _dbContext.ActivityTypes.Remove(activityType);
                await _dbContext.SaveChangesAsync();

                SetAlert("Activity Type deleted successfully.", "success");
            }

            return RedirectToAction(nameof(Index));
        }

        private bool ActivityTypeExists(int id)
        {
            return _dbContext.ActivityTypes.Any(e => e.Id == id);
        }
    }
}
