using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class TripController : BaseController
    {
        private readonly ITripMapThumbnailGenerator _thumbnailGenerator;
        private readonly ITripTagService _tripTagService;
        private readonly ICacheWarmupScheduler _warmupScheduler;
        private readonly TripCloneCoordinator _tripCloneCoordinator;

        public TripController(
            ILogger<TripController> logger,
            ApplicationDbContext dbContext,
            ITripMapThumbnailGenerator thumbnailGenerator,
            ITripTagService tripTagService,
            ICacheWarmupScheduler warmupScheduler,
            TripCloneCoordinator? tripCloneCoordinator = null)
            : base(logger, dbContext)
        {
            _thumbnailGenerator = thumbnailGenerator;
            _tripTagService = tripTagService;
            _warmupScheduler = warmupScheduler;
            _tripCloneCoordinator = tripCloneCoordinator ?? new TripCloneCoordinator(dbContext);
        }

        /// <summary>
        /// Shows the Trip index page shell. Trip data is loaded via AJAX
        /// from the <c>/api/Trips/search</c> endpoint with server-side pagination.
        /// </summary>
        public IActionResult Index()
        {
            SetPageTitle("My Trips");
            return View();
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
                .Include(t => t.Segments!).ThenInclude(s => s.FromPlace)
                .Include(t => t.Segments!).ThenInclude(s => s.ToPlace)
                .Include(t => t.Segments!).ThenInclude(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place)
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

        /// <summary>Commits a new owned Trip and its server-created fallback together before scheduling images.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind("Id,Name,Notes,IsPublic,ShareProgressEnabled,CenterLat,CenterLon,Zoom,CoverImageUrl")] Trip model,
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

            TripRichNotes.NormalizeDestination(model);
            model.UserId = userId;
            model.UpdatedAt = DateTime.UtcNow;

            // Remove any ModelState entries for User/UserId if necessary
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.User));

            if (ModelState.IsValid)
            {
                try
                {
                    // The form creates a Trip shell. Child graphs are server-owned, even for direct action callers.
                    model.Regions = [new Region
                    {
                        Id = Guid.NewGuid(), TripId = model.Id, UserId = userId,
                        Name = "Unassigned Places", DisplayOrder = 0
                    }];
                    model.Segments = [];
                    model.Tags = [];
                    _dbContext.Trips.Add(model);
                    await _dbContext.SaveChangesAsync(HttpContext.RequestAborted);
                }
                catch (Exception creationFailure)
                {
                    // HandleError saves its audit on this context. Discard the failed graph before any audit save.
                    _dbContext.ChangeTracker.Clear();
                    try { HandleError(creationFailure); }
                    catch (Exception auditFailure)
                    {
                        throw new AggregateException("Trip creation failed and error auditing also failed.",
                            creationFailure, auditFailure);
                    }
                    return View(model);
                }

                SetAlert("Trip created successfully!");
                try
                {
                    // Schedule immediate cache warm-up if the new trip has images
                    if (!string.IsNullOrWhiteSpace(model.CoverImageUrl)
                        || HtmlHelpers.ExtractExternalImageUrls(model.Notes).Any())
                    {
                        await _warmupScheduler.ScheduleWarmupAsync(model.Id, immediate: true);
                    }
                }
                catch (Exception warmupFailure)
                {
                    _logger.LogError(warmupFailure, "Trip {TripId} was created but cache warm-up scheduling failed.", model.Id);
                    SetAlert("Trip created, but image cache warm-up could not be scheduled.", "warning");
                }

                return submitAction == "save-edit"
                    ? RedirectToAction(nameof(Edit), "Trip", new { area = "User", id = model.Id })
                    : RedirectToAction(nameof(Index));
            }

            // ModelState invalid
            SetAlert("Could not create Trip", "danger");
            return View(model);
        }

        /// <summary>
        /// Shows the canonical Vue Trip Editor shell for an owned trip.
        /// </summary>
        public async Task<IActionResult> Edit(Guid id)
        {
            SetPageTitle("Edit Trip");
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var trip = await _dbContext.Trips
                .AsNoTracking()
                .Where(t => t.Id == id && t.UserId == userId)
                .Select(t => new { t.Id, t.Name })
                .FirstOrDefaultAsync();

            if (trip == null)
            {
                return NotFound();
            }

            ViewData["Title"] = "Trip Editor";
            ViewData["BodyClass"] = "container-fluid";
            ViewData["LoadLeaflet"] = false;
            ViewData["LoadQuill"] = false;

            return View("~/Areas/User/Views/Trip/Edit.cshtml", new TripEditorShellViewModel
            {
                TripId = trip.Id,
                TripName = trip.Name,
                EditorEndpointUrl = $"/api/trips/{trip.Id}/editor",
                TripIndexUrl = Url?.Action(nameof(Index), "Trip", new { area = "User" }) ?? "/User/Trip/Index",
                TilesUrl = "/Public/tiles/{z}/{x}/{y}.png"
            });
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
        /// Toggle the ShareProgressEnabled setting for a trip via AJAX.
        /// Enabling requires publication; either direction requires the current active owner at the write boundary.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleShareProgress(Guid id, bool enabled)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Json(new { success = false, error = "Trip not found or unauthorized" });

            // Share the editor's final Trip -> active-account lock boundary without changing the legacy JSON contract.
            var result = await new TripShareProgressMutationService(_dbContext)
                .ApplyAsync(id, userId, enabled, HttpContext.RequestAborted);
            if (result.Status is TripShareProgressStatus.NotFound or TripShareProgressStatus.Inactive)
                return Json(new { success = false, error = "Trip not found or unauthorized" });
            if (result.Status == TripShareProgressStatus.NotPublic)
                return Json(new { success = false, error = "Trip must be public to share progress" });

            return Json(new { success = true, enabled = result.Trip!.ShareProgressEnabled });
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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            TripCloneResult clone;
            try { clone = await _tripCloneCoordinator.CloneAsync(id, userId); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone trip {TripId}", id);
                SetAlert("Failed to clone trip. Please try again.", "danger");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }

            if (clone.Status == TripCloneStatus.NotFound)
            {
                SetAlert("Trip not found.", "warning");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }
            if (clone.Status == TripCloneStatus.NotPublic)
            {
                SetAlert("This trip is not public and cannot be cloned.", "danger");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }
            if (clone.Status == TripCloneStatus.AlreadyOwned)
            {
                SetAlert("You already own this trip. Use the duplicate feature instead.", "info");
                return RedirectToAction("Edit", new { id });
            }

            if (clone.RequiresImageWarmup)
            {
                try { await _warmupScheduler.ScheduleWarmupAsync(clone.ClonedTripId!.Value, immediate: true); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Trip clone {ClonedTripId} committed but cache warmup scheduling failed", clone.ClonedTripId);
                }
            }
            SetAlert($"Trip '{clone.SourceTripName}' has been cloned to your account!", "success");
            return RedirectToAction("Edit", new { id = clone.ClonedTripId });
        }
    }
}
