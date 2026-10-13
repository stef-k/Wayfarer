using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Npgsql;

namespace Wayfarer.Areas.Admin.Controllers
{
    /// <summary>Maintains activity types for administrators only.</summary>
    [Authorize(Roles = "Admin")]
    [Area("Admin")]
    public class ActivityTypeController : BaseController
    {
        /// <summary>Uses the current catalog context and existing Admin authorization.</summary>
        public ActivityTypeController(ILogger<ActivityTypeController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        { }

        /// <summary>Lists the shared catalog with existing search and pagination.</summary>
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


        /// <summary>Shows the existing create form.</summary>
        public IActionResult Create()
        {
            SetPageTitle("New Activity Type");
            return View();
        }

        /// <summary>Canonicalizes names and returns the submitted form after a safely discarded failed write.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,Description")] ActivityType activityType)
        {
            if (!ValidateModelState() || !await ValidateActivityAsync(activityType))
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
            catch (DbUpdateException ex) when (IsNameConflict(ex))
            {
                _dbContext.Entry(activityType).State = EntityState.Detached;
                ModelState.AddModelError(nameof(ActivityType.Name), "An Activity with this name already exists.");
                return View(activityType);
            }
            catch (DbUpdateException ex) when (IsNameCapacity(ex))
            {
                _dbContext.Entry(activityType).State = EntityState.Detached;
                ModelState.AddModelError(nameof(ActivityType.Name), "This Activity name is too large for the catalog index. Please choose a different name.");
                return View(activityType);
            }
            catch (Exception ex)
            {
                _dbContext.Entry(activityType).State = EntityState.Detached;
                HandlePersistenceError(ex);
                return View(activityType);
            }
        }

        /// <summary>Shows the existing edit form for a live catalog ID.</summary>
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            ActivityType? activityType = await _dbContext.ActivityTypes.FindAsync(id);
            return activityType == null ? (IActionResult)NotFound() : View(activityType);
        }

        /// <summary>Retains IDs/references on success and discards failed edits before validation or missing-entry responses.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,Description")] ActivityType activityType)
        {
            if (id != activityType.Id)
            {
                return NotFound();
            }

            if (!ValidateModelState()) return View(activityType);
            var current = await _dbContext.ActivityTypes.FindAsync(id);
            if (current == null) return NotFound();
            if (!await ValidateActivityAsync(activityType)) return View(activityType);

            try
            {
                current.Name = activityType.Name;
                current.Description = activityType.Description;
                await _dbContext.SaveChangesAsync();

                SetAlert("Activity Type updated successfully.", "success");
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException ex) when (IsNameConflict(ex))
            {
                _dbContext.Entry(current).State = EntityState.Detached;
                ModelState.AddModelError(nameof(ActivityType.Name), "An Activity with this name already exists.");
                return View(activityType);
            }
            catch (DbUpdateException ex) when (IsNameCapacity(ex))
            {
                _dbContext.Entry(current).State = EntityState.Detached;
                ModelState.AddModelError(nameof(ActivityType.Name), "This Activity name is too large for the catalog index. Please choose a different name.");
                return View(activityType);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _dbContext.Entry(current).State = EntityState.Detached;
                if (!await ActivityTypeExistsAsync(id)) return NotFound();
                HandlePersistenceError(ex);
                return View(activityType);
            }
            catch (Exception ex)
            {
                _dbContext.Entry(current).State = EntityState.Detached;
                HandlePersistenceError(ex);
                return View(activityType);
            }
        }

        /// <summary>Shows the existing delete confirmation.</summary>
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

        /// <summary>Preserves racing references and treats a concurrent winning delete as an already-missing entry.</summary>
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var activityType = await _dbContext.ActivityTypes.FindAsync(id);
            if (activityType == null) return RedirectToAction(nameof(Index));

            if (await _dbContext.Locations.AnyAsync(l => l.ActivityTypeId == id)
                || await _dbContext.Set<ActivityTypeIdRedirect>().AnyAsync(r => r.ActivityTypeId == id))
            {
                SetAlert("Activity cannot be deleted: Locations or retained client IDs reference it.", "warning");
                return RedirectToAction(nameof(Index));
            }

            try
            {
                _dbContext.ActivityTypes.Remove(activityType);
                await _dbContext.SaveChangesAsync();
                SetAlert("Activity Type deleted successfully.", "success");
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.ForeignKeyViolation,
                  ConstraintName: "FK_Locations_ActivityTypes_ActivityTypeId" or "FK_ActivityTypeIdRedirects_ActivityTypes_ActivityTypeId" })
            {
                _dbContext.Entry(activityType).State = EntityState.Unchanged;
                SetAlert("Activity cannot be deleted: Locations or retained client IDs reference it.", "warning");
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _dbContext.Entry(activityType).State = EntityState.Detached;
                if (await ActivityTypeExistsAsync(id)) HandlePersistenceError(ex);
            }
            catch (Exception ex)
            {
                _dbContext.Entry(activityType).State = EntityState.Detached;
                HandlePersistenceError(ex);
            }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Prechecks improve feedback; PostgreSQL still decides concurrent uniqueness.</summary>
        private async Task<bool> ValidateActivityAsync(ActivityType activity)
        {
            var name = await new ActivityCatalog(_dbContext).NormalizeAsync(activity.Name, HttpContext.RequestAborted);
            if (string.IsNullOrEmpty(name.DisplayName))
                ModelState.AddModelError(nameof(ActivityType.Name), "Enter a nonblank Activity name containing valid XML text.");
            else if (await _dbContext.ActivityTypes.AnyAsync(a => a.Id != activity.Id
                && EF.Property<string>(a, "NormalizedName") == name.NormalizedName))
                ModelState.AddModelError(nameof(ActivityType.Name), "An Activity with this name already exists.");
            else
                activity.Name = name.DisplayName;

            if (!ActivityCatalog.CanTransport(activity.Description))
                ModelState.AddModelError(nameof(ActivityType.Description), "Description contains invalid storage text.");
            return ModelState.IsValid;
        }

        /// <summary>Translates only the named catalog uniqueness constraint.</summary>
        private static bool IsNameConflict(DbUpdateException exception) =>
            exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_ActivityTypes_NormalizedName" };

        /// <summary>
        /// Recognizes catalog index capacity, not arbitrary program limits. Oversized tuple formation lacks
        /// object diagnostics; the Activity model's only text index is its generated name key.
        /// </summary>
        private static bool IsNameCapacity(DbUpdateException exception) =>
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ProgramLimitExceeded } postgres
            && exception.Entries.Count == 1 && exception.Entries[0].Entity is ActivityType
            && (postgres is { SchemaName: "public", TableName: "ActivityTypes", ConstraintName: "IX_ActivityTypes_NormalizedName" }
                || (postgres is { Routine: "index_form_tuple_context", SchemaName: null, TableName: null, ConstraintName: null }
                    && (exception.Entries[0].State == EntityState.Added
                        || exception.Entries[0].Property(nameof(ActivityType.Name)).IsModified)));

        /// <summary>Logs only safe failure metadata and uses the existing alert without saving the failed context.</summary>
        private void HandlePersistenceError(Exception exception)
        {
            _logger.LogError("An error occurred during an action: {ExceptionType}; request {RequestId}",
                exception.GetType().Name, HttpContext.TraceIdentifier);
            SetAlert("An unexpected error occurred. Please try again later.", "danger");
        }

        /// <summary>Checks existence after discarding the stale write; an unavailable lookup uses the failed-operation response.</summary>
        private async Task<bool> ActivityTypeExistsAsync(int id)
        {
            try { return await _dbContext.ActivityTypes.AnyAsync(e => e.Id == id); }
            catch (Exception exception)
            {
                _logger.LogWarning("Activity existence check failed: {ExceptionType}; request {RequestId}",
                    exception.GetType().Name, HttpContext.TraceIdentifier);
                return true; // Unknown existence must not be reported as a confirmed missing entry.
            }
        }
    }
}
