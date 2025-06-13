using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class TripController : BaseController
    {
        public TripController(ILogger<TripController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
        }

        /// <summary>
        /// Shows all user's Trips
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> Index()
        {
            try
            {
                SetPageTitle("My Trips");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                var trips = await _dbContext.Trips
                    .Where(t => t.UserId == userId)
                    .OrderByDescending(t => t.StartDate)
                    .ToListAsync();

                return View(trips);
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View(new List<Trip>()); // return empty list on error
            }
        }

        // GET: /User/Trip/Create
        public IActionResult Create()
        {
            SetPageTitle("New Trip");
            return View(new Trip
            {
                Id = Guid.NewGuid(),
                IsPublic = false
            });
        }

        // POST: /User/Trip/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            Trip model,
            [FromForm] string submitAction // will be "save" (default) or "save-edit"
        )
        {
            SetPageTitle("New Trip");

            // Assign owner and timestamp
            model.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            model.UpdatedAt = DateTime.UtcNow;

            // Remove any ModelState entries for User/UserId if necessary
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.User));

            if (ModelState.IsValid)
            {
                try
                {
                    // Persist
                    _dbContext.Trips.Add(model);
                    await _dbContext.SaveChangesAsync();

                    SetAlert("Trip created successfully!");

                    // Branch on which button was clicked
                    if (submitAction == "save-edit")
                    {
                        // Redirect to Edit for further configuration
                        return RedirectToAction(
                            actionName: nameof(Edit),
                            controllerName: ControllerContext.ActionDescriptor.ControllerName,
                            routeValues: new { area = "User", id = model.Id }
                        );
                    }

                    // Default: back to list
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    return View(model);
                }
            }

            // ModelState invalid
            SetAlert("Could not create Trip", "danger");
            return View(model);
        }

        // GET: /User/Trip/Edit/{id}
        public async Task<IActionResult> Edit(Guid id)
        {
            SetPageTitle("Edit Trip");
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // 1) Eager-load Regions → Places and Segments
            var trip = await _dbContext.Trips.Where(t => t.Id == id)
                .Include(t => t.Regions)
                .ThenInclude(r => r.Places)
                .Include(t => t.Segments)
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

            if (trip == null)
            {
                SetAlert("Trip not found.", "warning");
                return RedirectToAction(nameof(Index));
            }

            // 2) Materialize to concrete Lists
            trip.Regions = trip.Regions?.ToList() ?? new List<Region>();
            foreach (var region in trip.Regions)
            {
                region.Places = region.Places?.ToList() ?? new List<Place>();
            }

            trip.Segments = trip.Segments?.ToList() ?? new List<Segment>();

            return View(trip);
        }

        // POST: /User/Trip/Edit/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            Guid id,
            Trip model,
            [FromForm] string? submitAction
        )
        {
            SetPageTitle("Edit Trip");

            if (id != model.Id)
            {
                SetAlert("ID mismatch.", "danger");
                return RedirectToAction(nameof(Index));
            }

            // Ensure only the owner can edit
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (model.UserId != userId)
            {
                SetAlert("Unauthorized.", "danger");
                return RedirectToAction(nameof(Index));
            }

            // Cleanup server-only props
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.User));

            if (!ValidateModelState())
                return View(model);

            try
            {
                // Fetch the tracked entity
                var trip = await _dbContext.Trips.FindAsync(id);
                if (trip == null)
                {
                    SetAlert("Trip not found.", "warning");
                    return RedirectToAction(nameof(Index));
                }

                // Update editable fields
                trip.Name = model.Name;
                trip.StartDate = model.StartDate;
                trip.EndDate = model.EndDate;
                trip.Days = model.Days;
                trip.IsPublic = model.IsPublic;
                trip.NotesHtml = model.NotesHtml;
                trip.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                SetAlert("Trip updated successfully!");

                if (submitAction == "save-edit")
                {
                    // Stay on edit page
                    return RedirectToAction(nameof(Edit), new { id });
                }
                else
                {
                    // Back to list
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View(model);
            }
        }
        
        [HttpGet]
        public async Task<IActionResult> GetTripDays(Guid tripId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var trip = await _dbContext.Trips
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

            if (trip == null)
                return NotFound();

            return Content(trip.Days?.ToString() ?? string.Empty);
        }

    }
}