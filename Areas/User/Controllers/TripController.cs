using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class TripController : BaseController
    {
        private readonly ITripMapThumbnailGenerator _thumbnailGenerator;
        private readonly ITripTagService _tripTagService;

        public TripController(
            ILogger<TripController> logger,
            ApplicationDbContext dbContext,
            ITripMapThumbnailGenerator thumbnailGenerator,
            ITripTagService tripTagService)
            : base(logger, dbContext)
        {
            _thumbnailGenerator = thumbnailGenerator;
            _tripTagService = tripTagService;
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
                .Include(t => t.Regions!).ThenInclude(a => a.Areas)
                .Include(t => t.Segments!)
                .Include(t => t.Tags)
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

            if (trip == null) return NotFound();

            /* ---- layout flags ---- */
            ViewData["LoadLeaflet"] = true;      // needs map
            ViewData["LoadQuill"]   = false;     // no editor
            ViewData["BodyClass"]   = "container-fluid";  // full-width

            ViewBag.IsOwner = true;
            ViewBag.IsEmbed = false;             // not an iframe here
            ViewBag.ShareProgressEnabled = trip.ShareProgressEnabled;

            // Calculate visit progress for owner's view
            var allPlaceIds = (trip.Regions ?? Enumerable.Empty<Region>())
                .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                .Select(p => p.Id)
                .ToList();

            // Get visit counts per place (a place can be visited multiple times)
            var visitEvents = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId && v.PlaceId != null && allPlaceIds.Contains(v.PlaceId.Value))
                .ToListAsync();

            var placeVisitCounts = visitEvents
                .GroupBy(v => v.PlaceId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            ViewBag.TotalPlaces = allPlaceIds.Count;
            ViewBag.VisitedPlaces = placeVisitCounts.Count;
            ViewBag.PlaceVisitCounts = placeVisitCounts;
            ViewBag.VisitEvents = visitEvents; // Pass flat list for modal

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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            model.UserId = userId;
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

            // 1) Eager-load Regions → Places, Areas and Segments
            var trip = await _dbContext.Trips.Where(t => t.Id == id)
                .Include(t => t.Regions)
                .ThenInclude(r => r.Places)
                .Include(t => t.Regions!).ThenInclude(a => a.Areas)
                .Include(t => t.Segments)
                .Include(t => t.Tags)
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

            // 3) Calculate visit progress for this trip
            var allPlaceIds = trip.Regions
                .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                .Select(p => p.Id)
                .ToList();

            // Get visit events per place (a place can be visited multiple times)
            var visitEvents = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId && v.PlaceId != null && allPlaceIds.Contains(v.PlaceId.Value))
                .OrderByDescending(v => v.ArrivedAtUtc)
                .ToListAsync();

            var placeVisitCounts = visitEvents
                .GroupBy(v => v.PlaceId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            ViewBag.TotalPlaces = allPlaceIds.Count;
            ViewBag.VisitedPlaces = placeVisitCounts.Count;
            ViewBag.PlaceVisitCounts = placeVisitCounts;
            ViewBag.VisitEvents = visitEvents; // Pass flat list, group in view

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
                // Reset ShareProgressEnabled when making trip private
                trip.ShareProgressEnabled = model.IsPublic && model.ShareProgressEnabled;
                trip.Notes = model.Notes;
                trip.CenterLat = model.CenterLat;
                trip.CenterLon = model.CenterLon;
                trip.Zoom = model.Zoom;
                trip.CoverImageUrl = model.CoverImageUrl;
                var updatedAt = DateTime.UtcNow;
                trip.UpdatedAt = updatedAt;

                await _dbContext.SaveChangesAsync();

                // Invalidate old thumbnails (will regenerate on next request)
                _thumbnailGenerator.InvalidateThumbnails(id, updatedAt);

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
                await _tripTagService.RemoveOrphanTagsAsync();

                // Clean up associated thumbnails
                _thumbnailGenerator.DeleteThumbnails(id);

                SetAlert("Trip deleted successfully!");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return RedirectToAction(nameof(Index));
            }
        }

        /// <summary>
        /// Clones a public trip to the current user's account.
        /// Creates a deep copy including all regions, places, areas, and segments.
        /// </summary>
        /// <param name="id">ID of the public trip to clone</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Clone(Guid id)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (string.IsNullOrEmpty(userId))
                {
                    SetAlert("User not authenticated.", "danger");
                    return RedirectToAction("Index", "Home", new { area = "" });
                }

                // Load the source trip with all related data
                var sourceTripQuery = _dbContext.Trips
                    .Include(t => t.Regions!)
                        .ThenInclude(r => r.Places)
                    .Include(t => t.Regions!)
                        .ThenInclude(r => r.Areas)
                    .Include(t => t.Segments)
                    .Include(t => t.Tags);

                var sourceTrip = await sourceTripQuery.FirstOrDefaultAsync(t => t.Id == id);

                if (sourceTrip == null)
                {
                    SetAlert("Trip not found.", "warning");
                    return RedirectToAction("Index", "TripViewer", new { area = "Public" });
                }

                // Only allow cloning public trips
                if (!sourceTrip.IsPublic)
                {
                    SetAlert("This trip is not public and cannot be cloned.", "danger");
                    return RedirectToAction("Index", "TripViewer", new { area = "Public" });
                }

                // Don't allow cloning your own trip
                if (sourceTrip.UserId == userId)
                {
                    SetAlert("You already own this trip. Use the duplicate feature instead.", "info");
                    return RedirectToAction("Edit", new { id = sourceTrip.Id });
                }

                // Create the cloned trip
                var clonedTrip = new Trip
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = $"{sourceTrip.Name} (Copy)",
                    Notes = sourceTrip.Notes,
                    IsPublic = false, // Start as private
                    CenterLat = sourceTrip.CenterLat,
                    CenterLon = sourceTrip.CenterLon,
                    Zoom = sourceTrip.Zoom,
                    CoverImageUrl = sourceTrip.CoverImageUrl,
                    UpdatedAt = DateTime.UtcNow
                };

                // Track old -> new ID mappings for places (needed for segments)
                var placeIdMapping = new Dictionary<Guid, Guid>();

                // Clone regions with places and areas
                if (sourceTrip.Regions != null)
                {
                    foreach (var sourceRegion in sourceTrip.Regions)
                    {
                        var clonedRegion = new Region
                        {
                            Id = Guid.NewGuid(),
                            UserId = userId,
                            TripId = clonedTrip.Id,
                            Name = sourceRegion.Name,
                            Notes = sourceRegion.Notes,
                            DisplayOrder = sourceRegion.DisplayOrder,
                            CoverImageUrl = sourceRegion.CoverImageUrl,
                            Center = sourceRegion.Center // NetTopologySuite Point is cloned by reference
                        };

                        // Clone places within this region
                        if (sourceRegion.Places != null)
                        {
                            foreach (var sourcePlace in sourceRegion.Places)
                            {
                                var newPlaceId = Guid.NewGuid();
                                placeIdMapping[sourcePlace.Id] = newPlaceId;

                                var clonedPlace = new Place
                                {
                                    Id = newPlaceId,
                                    UserId = userId,
                                    RegionId = clonedRegion.Id,
                                    Name = sourcePlace.Name,
                                    Location = sourcePlace.Location,
                                    Notes = sourcePlace.Notes,
                                    DisplayOrder = sourcePlace.DisplayOrder,
                                    IconName = sourcePlace.IconName,
                                    MarkerColor = sourcePlace.MarkerColor,
                                    Address = sourcePlace.Address
                                };

                                clonedRegion.Places!.Add(clonedPlace);
                            }
                        }

                        // Clone areas within this region
                        if (sourceRegion.Areas != null)
                        {
                            foreach (var sourceArea in sourceRegion.Areas)
                            {
                                var clonedArea = new Area
                                {
                                    Id = Guid.NewGuid(),
                                    RegionId = clonedRegion.Id,
                                    Name = sourceArea.Name,
                                    Notes = sourceArea.Notes,
                                    DisplayOrder = sourceArea.DisplayOrder,
                                    FillHex = sourceArea.FillHex,
                                    Geometry = sourceArea.Geometry // NetTopologySuite Polygon
                                };

                                clonedRegion.Areas.Add(clonedArea);
                            }
                        }

                        clonedTrip.Regions!.Add(clonedRegion);
                    }
                }

                // Clone segments with updated place references
                if (sourceTrip.Segments != null)
                {
                    foreach (var sourceSegment in sourceTrip.Segments)
                    {
                        var clonedSegment = new Segment
                        {
                            Id = Guid.NewGuid(),
                            UserId = userId,
                            TripId = clonedTrip.Id,
                            Mode = sourceSegment.Mode,
                            RouteGeometry = sourceSegment.RouteGeometry,
                            EstimatedDuration = sourceSegment.EstimatedDuration,
                            EstimatedDistanceKm = sourceSegment.EstimatedDistanceKm,
                            DisplayOrder = sourceSegment.DisplayOrder,
                            Notes = sourceSegment.Notes,
                            // Map old place IDs to new place IDs
                            FromPlaceId = sourceSegment.FromPlaceId.HasValue && placeIdMapping.ContainsKey(sourceSegment.FromPlaceId.Value)
                                ? placeIdMapping[sourceSegment.FromPlaceId.Value]
                                : null,
                            ToPlaceId = sourceSegment.ToPlaceId.HasValue && placeIdMapping.ContainsKey(sourceSegment.ToPlaceId.Value)
                                ? placeIdMapping[sourceSegment.ToPlaceId.Value]
                                : null
                        };

                        clonedTrip.Segments!.Add(clonedSegment);
                    }
                }

                // Save the cloned trip to database
                if (sourceTrip.Tags?.Count > 0)
                {
                    foreach (var tag in sourceTrip.Tags)
                    {
                        clonedTrip.Tags.Add(tag);
                    }
                }

                _dbContext.Trips.Add(clonedTrip);
                await _dbContext.SaveChangesAsync();

                SetAlert($"Trip '{sourceTrip.Name}' has been cloned to your account!", "success");
                return RedirectToAction("Edit", new { id = clonedTrip.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone trip {TripId}", id);
                SetAlert("Failed to clone trip. Please try again.", "danger");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }
        }
    }
}
