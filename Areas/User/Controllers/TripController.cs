using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public partial class TripController : BaseController
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

        /// <summary>Private Layout B shell; the composition API authorizes every saved workspace read and action.</summary>
        [HttpGet]
        public IActionResult Compose(Guid? id)
        {
            ViewData["Title"] = "Compare & combine Trips";
            ViewData["BodyClass"] = "container-fluid";
            ViewData["CompositionId"] = id;
            Response.Headers.CacheControl = "private, no-store";
            return View();
        }
        
        /// <summary>Lightweight copy shell reuses shared choice controls, without creating a comparison or another Trip.</summary>
        [HttpGet]
        public IActionResult Copy(Guid? source, Guid? destination)
        {
            ViewData["Title"] = "Copy to existing Trip";
            ViewData["CopyMode"] = true;
            ViewData["CopySource"] = source;
            ViewData["CopyDestination"] = destination;
            Response.Headers.CacheControl = "private, no-store";
            return View("Compose");
        }

        /// <summary>Loads the owned Trip graph and visit data from one snapshot before rendering the private Viewer.</summary>
        [HttpGet]
        public async Task<IActionResult> View(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Forbid();

            var ct = HttpContext.RequestAborted;
            Trip? trip;
            List<Guid> allPlaceIds;
            List<PlaceVisitEvent> visitEvents;
            // Each split read, including private visit data, must see the same saved Trip.
            await using (var snapshot = _dbContext.Database.IsRelational()
                ? await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct) : null)
            {
                trip = await _dbContext.Trips
                    .AsSplitQuery()
                    .Include(t => t.Regions!).ThenInclude(r => r.Places!)
                    .Include(t => t.Regions!).ThenInclude(r => r.Areas)
                    .Include(t => t.Segments!).ThenInclude(s => s.FromPlace)
                    .Include(t => t.Segments!).ThenInclude(s => s.ToPlace)
                    .Include(t => t.Segments!).ThenInclude(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place)
                    .Include(t => t.Tags)
                    .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
                if (trip == null) return NotFound();

                allPlaceIds = (trip.Regions ?? Enumerable.Empty<Region>())
                    .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                    .Select(p => p.Id)
                    .ToList();
                visitEvents = await _dbContext.PlaceVisitEvents
                    .Where(v => v.UserId == userId && v.PlaceId != null && allPlaceIds.Contains(v.PlaceId.Value))
                    .ToListAsync(ct);
            }

            var placeVisitCounts = visitEvents.GroupBy(v => v.PlaceId!.Value).ToDictionary(g => g.Key, g => g.Count());
            ViewData["LoadLeaflet"] = true;
            ViewData["BodyClass"] = "container-fluid";
            ViewBag.IsOwner = true;
            ViewBag.IsEmbed = false;
            ViewBag.ShareProgressEnabled = trip.ShareProgressEnabled;
            ViewBag.TotalPlaces = allPlaceIds.Count;
            ViewBag.VisitedPlaces = placeVisitCounts.Count;
            ViewBag.PlaceVisitCounts = placeVisitCounts;
            ViewBag.VisitEvents = visitEvents;

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
                    try { HandleError(creationFailure, "Editor.TripsCouldNotCreateTrip"); }
                    catch (Exception auditFailure)
                    {
                        throw new AggregateException("Trip creation failed and error auditing also failed.",
                            creationFailure, auditFailure);
                    }
                    return View(model);
                }

                SetAlert("Trip created successfully!", "success", "Editor.TripsTripCreatedSuccessfully");
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
                    SetAlert("Trip created, but image cache warm-up could not be scheduled.", "warning", "Editor.TripsTripCreatedButImageCacheWarm");
                }

                return submitAction == "save-edit"
                    ? RedirectToAction(nameof(Edit), "Trip", new { area = "User", id = model.Id })
                    : RedirectToAction(nameof(Index));
            }

            // ModelState invalid
            SetAlert("Could not create Trip", "danger", "Editor.TripsCouldNotCreateTrip");
            return View(model);
        }

        /// <summary>
        /// Shows the canonical Vue Trip Editor shell for an owned trip.
        /// </summary>
        public async Task<IActionResult> Edit(Guid id, Guid? composition = null)
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

            // Return context must belong to this caller and this C; arbitrary return URLs are never accepted.
            var comparisonUrl = composition.HasValue && await _dbContext.Set<TripComposition>().AnyAsync(w =>
                w.Id == composition && w.TripId == id && w.UserId == userId && w.FinishedAt == null)
                ? $"/User/Trip/Compose/{composition.Value}" : null;
            ViewData["CompositionReturnUrl"] = comparisonUrl;

            return View("~/Areas/User/Views/Trip/Edit.cshtml", new TripEditorShellViewModel
            {
                TripId = trip.Id,
                TripName = trip.Name,
                EditorEndpointUrl = $"/api/trips/{trip.Id}/editor",
                TripIndexUrl = comparisonUrl ?? Url?.Action(nameof(Index), "Trip", new { area = "User" }) ?? "/User/Trip/Index",
                TilesUrl = "/Public/tiles/{z}/{x}/{y}.png"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(Guid id)
        {
            // The list needs the mutation result. Ordinary forms still receive destination-localized alerts.
            var listRequest = Request.Headers.Accept == "application/json";
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                // Join the durable comparison barrier before normal root/cascade deletion.
                if (userId == null || !await TripCompositionLifecycle.DeleteAsync(_dbContext, id, userId, HttpContext.RequestAborted))
                {
                    if (listRequest) return NotFound();
                    SetAlert("Trip not found or unavailable.", "warning", "Editor.TripsTripNotFoundOrUnavailable");
                    return RedirectToAction(nameof(Index));
                }
                await _tripTagService.RemoveOrphanTagsAsync();

                // Clean up associated thumbnails
                _thumbnailGenerator.DeleteThumbnails(id);

                if (listRequest) return NoContent();
                SetAlert("Trip deleted successfully!", "success", "Editor.TripsTripDeletedSuccessfully");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex);
                if (listRequest) return StatusCode(StatusCodes.Status500InternalServerError);
                return RedirectToAction(nameof(Index));
            }
        }

        /// <summary>Refuses a Viewer sharing write from a page rendered before cutover.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> ToggleShareProgress(Guid id, bool enabled) =>
            Task.FromResult(TripMetadataReceiptRoutes.UpgradeRequired().ToActionResult());

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
                SetCloneAlert("Viewer.CloneSignIn", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            TripCloneResult clone;
            try { clone = await _tripCloneCoordinator.CloneAsync(id, userId); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone trip {TripId}", id);
                SetCloneAlert("Viewer.CloneFailed", "danger");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }

            if (clone.Status == TripCloneStatus.NotFound)
            {
                SetCloneAlert("Viewer.CloneMissing", "warning");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }
            if (clone.Status == TripCloneStatus.NotPublic)
            {
                SetCloneAlert("Viewer.ClonePrivate", "danger");
                return RedirectToAction("Index", "TripViewer", new { area = "Public" });
            }
            if (clone.Status == TripCloneStatus.AlreadyOwned)
            {
                SetCloneAlert("Viewer.CloneOwned", "info");
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
            SetCloneAlert(clone.MediaOmitted ? "Viewer.ClonePartial" : "Viewer.CloneSucceeded", "success", clone.SourceTripName);
            return RedirectToAction("Edit", new { id = clone.ClonedTripId });
        }

        /// <summary>Defers clone feedback formatting until the destination resolves its request locale.</summary>
        private void SetCloneAlert(string key, string type, string? argument = null)
        {
            TempData["WebAlertKey"] = key;
            TempData["WebAlertArgument"] = argument;
            TempData["AlertType"] = type;
        }
    }
}
