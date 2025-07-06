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
                    .OrderByDescending(t => t.UpdatedAt)
                    .ToListAsync();

                return View(trips);
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View(new List<Trip>()); // return empty list on error
            }
        }
        
        // GET: /User/Trips/View/{id}
        [HttpGet]
        public async Task<IActionResult> View(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Forbid(); 
            
            var trip = await _dbContext.Trips
                .Include(t => t.Regions!).ThenInclude(r => r.Places!)
                .Include(t => t.Segments!)
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

            if (trip == null) return NotFound(); 
            
            /* ---- layout flags ---- */
            ViewData["LoadLeaflet"] = true;      // needs map
            ViewData["LoadQuill"]   = false;     // no editor
            ViewData["BodyClass"]   = "container-fluid";  // full-width

            ViewBag.IsOwner = true;
            ViewBag.IsEmbed = false;             // not an iframe here

            return View("~/Views/Trip/Viewer.cshtml", trip);
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
                    
                    // ✅ Always create shadow region after trip insert
                    var shadowRegion = new Region
                    {
                        Id = Guid.NewGuid(),
                        TripId = model.Id,
                        UserId = model.UserId,
                        Name = "Unassigned Places",         // 🧭 Do not allow edit in UI
                        DisplayOrder = 0,                   // 🥇 Always shown at top
                        Notes = null,
                        Center = null,
                        CoverImageUrl = null
                    };
                    _dbContext.Regions.Add(shadowRegion);
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

            // Check ID mismatch
            if (id != model.Id)
            {
                SetAlert("ID mismatch.", "danger");
                return RedirectToAction(nameof(Index));
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Remove client-posted values that should not be trusted
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.User));

            if (!ValidateModelState())
                return View(model);

            try
            {
                // Load actual Trip from DB to ensure ownership and prevent overwrite
                var trip = await _dbContext.Trips.FindAsync(id);

                if (trip == null || trip.UserId != userId)
                {
                    SetAlert("Unauthorized or trip not found.", "danger");
                    return RedirectToAction(nameof(Index));
                }

                // Update editable fields only
                trip.Name = model.Name;
                trip.IsPublic = model.IsPublic;
                trip.Notes = model.Notes;
                trip.CenterLat = model.CenterLat;
                trip.CenterLon = model.CenterLon;
                trip.Zoom = model.Zoom;
                trip.CoverImageUrl = model.CoverImageUrl;
                trip.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                SetAlert("Trip updated successfully!");

                return submitAction == "save-edit"
                    ? RedirectToAction(nameof(Edit), new { id })
                    : RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View(model);
            }
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(Guid id)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                var trip = await _dbContext.Trips.FindAsync(id);
                if (trip == null)
                {
                    SetAlert("Trip not found.", "warning");
                    return RedirectToAction(nameof(Index));
                }

                if (trip.UserId != userId)
                {
                    SetAlert("Unauthorized access.", "danger");
                    return RedirectToAction(nameof(Index));
                }

                _dbContext.Trips.Remove(trip);
                await _dbContext.SaveChangesAsync();

                SetAlert("Trip deleted successfully!");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return RedirectToAction(nameof(Index));
            }
        }
    }
}