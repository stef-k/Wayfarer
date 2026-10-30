using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class LocationController : BaseController
    {
        private readonly ReverseGeocodingService _reverseGeocodingService;
        private readonly SseService _sse;
        private readonly IPlaceVisitDetectionService _placeVisitDetectionService;

        public LocationController(ILogger<BaseController> logger, ApplicationDbContext dbContext,
            ReverseGeocodingService reverseGeocodingService, SseService sse,
            IPlaceVisitDetectionService placeVisitDetectionService)
            : base(logger, dbContext)
        {
            _reverseGeocodingService = reverseGeocodingService;
            _sse = sse;
            _placeVisitDetectionService = placeVisitDetectionService;
        }

        /// <summary>
        /// Displays the user's location history in a list or map format.
        /// </summary>
        /// <returns>The location history view.</returns>
        [HttpGet]
        public IActionResult Index()
        {
            SetPageTitle("Location Management");
            return View(); // Return the view without passing the model
        }

        /// <summary>
        /// Displays all user locations in a table format for single and also mass CRUD operations 
        /// </summary>
        /// <returns></returns>
        public IActionResult AllLocations()
        {
            SetPageTitle("Location Management");
            return View();
        }

        /// <summary>
        /// Displays a form to create a new location entry.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Retrieve the list of activity types from the database
            List<ActivityType> activityTypes = await _dbContext.ActivityTypes.ToListAsync();

            // Find the default activity type (e.g., Walking)
            ActivityType? defaultActivity = activityTypes.FirstOrDefault(a => a.Name == "Walking");

            // Create a view model to pass to the view
            AddLocationViewModel viewModel = new AddLocationViewModel
            {
                UserId = userId,
                ActivityTypes = activityTypes.Select(a => new SelectListItem
                {
                    Value = a.Id.ToString(),
                    Text = a.Name,
                    Selected = a.Id == defaultActivity?.Id // Set the default selected item
                }).ToList(),
                LocalTimestamp = DateTime.Now // Set the default local timestamp
            };

            SetPageTitle("Add Location");
            return View(viewModel);
        }

        /// <summary>
        /// Adds a new location from UI to the user's timeline.
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AddLocationViewModel model)
        {
            ModelState.Remove("ActivityTypes");
            try
            {
                if (!ModelState.IsValid)
                {
                    string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                    // Retrieve the list of activity types from the database
                    List<ActivityType> activityTypes = await _dbContext.ActivityTypes.ToListAsync();

                    // Find the default activity type (e.g., Walking)
                    ActivityType? defaultActivity = activityTypes.FirstOrDefault(a => a.Name == "Walking");

                    // Create a view model to pass to the view (only SelectedActivityId needs to be passed here)
                    AddLocationViewModel viewModel = new AddLocationViewModel
                    {
                        UserId = userId,
                        Latitude = model.Latitude,
                        Longitude = model.Longitude,
                        ActivityTypes = activityTypes.Select(a => new SelectListItem
                        {
                            Value = a.Id.ToString(),
                            Text = a.Name,
                            Selected = a.Id == defaultActivity?.Id // Set the default selected item
                        }).ToList(),
                        Notes = RichNotes.Normalize(model.Notes),
                        Accuracy = model.Accuracy,
                        Speed = model.Speed,
                        Address = model.Address,
                        FullAddress = model.FullAddress,
                        AddressNumber = model.AddressNumber,
                        StreetName = model.StreetName,
                        PostCode = model.PostCode,
                        Place = model.Place,
                        Region = model.Region,
                        Country = model.Country,
                        SelectedActivityId = model.SelectedActivityId, // Make sure we pass the selected ID correctly
                        LocalTimestamp = DateTime.Now // Set the default local timestamp
                    };

                    SetPageTitle("Add Location");
                    SetAlert("Could not save location", "danger");

                    return View(viewModel);
                }

                // Associate the location with the current user
                model.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (model.UserId == null) return Unauthorized();
                var user = await _dbContext.ApplicationUsers.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == model.UserId);

                DateTime Timestamp = DateTime.UtcNow;
                // Map the ViewModel to the Location entity
                var utc = CoordinateTimeZoneConverter.ConvertToUtc(model.Latitude, model.Longitude,
                    model.LocalTimestamp);

                Location location = new Location
                {
                    UserId = model.UserId,
                    Timestamp = Timestamp, // Server-side timestamp
                    LocalTimestamp = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                    TimeZoneId =
                        CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(model.Latitude, model.Longitude),
                    Coordinates =
                        new NetTopologySuite.Geometries.Point(model.Longitude, model.Latitude)
                            { SRID = 4326 }, // Create Point, in PostGIS lon comes first then lat!
                    ActivityTypeId = await new ActivityCatalog(_dbContext).ResolveWriteIdAsync(model.SelectedActivityId, HttpContext.RequestAborted), // Accept retained stale-form IDs
                    Accuracy = model.Accuracy,
                    Altitude = model.Altitude,
                    Speed = model.Speed,
                    Address = model.Address,
                    Notes = RichNotes.Normalize(model.Notes)
                };


                if (string.IsNullOrWhiteSpace(model.Address))
                {
                    var enrichment = await _reverseGeocodingService.EnrichAsync(model.UserId,
                        location.Coordinates.Y, location.Coordinates.X, ReverseGeocodingIntent.LocationCreate,
                        HttpContext.RequestAborted);
                    enrichment.ApplyTo(location, DateTimeOffset.UtcNow);
                }

                // Save the location to the database
                _dbContext.Locations.Add(location);
                await _dbContext.SaveChangesAsync();

                // A web map-selected point records history without asserting a sensor observation or arrival visit.
                LogAction("CreateLocation", $"Location added for user {model.UserId}");
                SetAlert("The location has been successfully created.", "success");

                // Per-user broadcast for timeline views (User/Timeline, Public/Timeline, Embed)
                await _sse.BroadcastAsync($"location-update-{user?.UserName ?? model.UserId}", JsonSerializer.Serialize(new
                {
                    LocationId = location.Id,
                    TimeStamp = location.Timestamp,
                    isLive = false,
                }));

                return RedirectToAction("Edit", new { location.Id });
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return StatusCode(500,
                    new { success = false, message = "An error occurred while adding the location." });
            }
        }


        /// <summary>
        /// Displays a form to edit an existing location entry.
        /// </summary>
        /// <param name="id">The ID of the location to edit.</param>
        /// <returns>The Edit view with the location data.</returns>
        [HttpGet]
        public async Task<IActionResult> Edit(int id, string? returnUrl = null)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Unauthorized();

            _logger.LogDebug("Edit[GET] start for LocationId={Id}, UserId={User}", id, userId);

            try
            {
                await using var snapshot = _dbContext.Database.IsRelational()
                    ? await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, HttpContext.RequestAborted) : null;
                var location = await _dbContext.Locations
                    .Include(l => l.ActivityType)
                    .FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId);

                if (location == null)
                {
                    _logger.LogWarning("Edit[GET] Location not found (Id={Id}, User={User})", id, userId);
                    SetAlert("Location not found.", "danger");
                    return RedirectToAction("Index");
                }

                var activityTypes = await _dbContext.ActivityTypes.ToListAsync();

                DateTime localTs;
                try
                {
                    localTs = CoordinateTimeZoneConverter.ConvertUtcToLocal(
                        location.Coordinates.Y,
                        location.Coordinates.X,
                        location.LocalTimestamp);
                }
                catch (Exception tzEx)
                {
                    _logger.LogError(tzEx,
                        "Edit[GET] Timezone conversion failed for coords ({Lat},{Lng}) TZ={TzId}",
                        location.Coordinates.Y,
                        location.Coordinates.X,
                        location.TimeZoneId);
                    throw;
                }

                var viewModel = new AddLocationViewModel
                {
                    Id = location.Id,
                    Latitude = location.Coordinates.Y,
                    Longitude = location.Coordinates.X,
                    Altitude = location.Altitude,
                    Address = location.Address,
                    SelectedActivityId = location.ActivityTypeId,
                    ActivityTypes = activityTypes.Select(a => new SelectListItem
                    {
                        Value = a.Id.ToString(),
                        Text = a.Name,
                        Selected = a.Id == location.ActivityTypeId
                    }).ToList(),
                    LocalTimestamp = localTs,
                    TimeZoneId = location.TimeZoneId,
                    Notes = RichNotes.Normalize(location.Notes),
                    FullAddress = location.FullAddress,
                    AddressNumber = location.AddressNumber,
                    StreetName = location.StreetName,
                    PostCode = location.PostCode,
                    Place = location.Place,
                    Region = location.Region,
                    Country = location.Country,
                };

                LocationManualAddressEdit.RestoreServerFields(viewModel, location);
                viewModel.ReturnUrl = GetSafeReturnUrl(returnUrl);

                if (_dbContext.Database.IsNpgsql())
                {
                    var detail = await HistoryDetail.ReadAsync(_dbContext, userId, id, NodaTime.DateTimeZone.Utc,
                        HttpContext.RequestAborted, HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
                    ViewData["LocationDetail"] = JsonSerializer.Serialize(detail);
                    ViewData["InputTimeZone"] = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(location.Coordinates.Y, location.Coordinates.X);
                }
                SetPageTitle("Edit Location");
                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edit[GET] Unhandled exception for LocationId={Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Updates an existing location in the user's timeline.
        /// </summary>
        /// <param name="model">The updated location data.</param>
        /// <returns>The updated location view or the list of locations.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(AddLocationViewModel model, string? saveAction)
        {
            return await Task.FromResult(StatusCode(428, new { code = "protectedMutationRequired" }));
        }

        private string GetSafeReturnUrl(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return returnUrl;
            }

            return Url.Action("Index", "Location", new { area = "User" }) ?? "/User/Location";
        }

        [HttpGet]
        public async Task<IActionResult> BulkEditNotes()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Unauthorized();

            // Pull distinct filter values for this user
            var query = _dbContext.Locations
                .Where(l => l.UserId == userId);

            var vm = new BulkEditNotesViewModel
            {
                Countries = await query
                    .Select(l => l.Country!)
                    .Distinct()
                    .OrderBy(c => c)
                    .Select(c => new SelectListItem(c, c))
                    .ToListAsync(),

                Regions = await query
                    .Select(l => l.Region!)
                    .Distinct()
                    .OrderBy(r => r)
                    .Select(r => new SelectListItem(r, r))
                    .ToListAsync(),

                Places = await query
                    .Select(l => l.Place!)
                    .Distinct()
                    .OrderBy(p => p)
                    .Select(p => new SelectListItem(p, p))
                    .ToListAsync()
            };

            SetPageTitle("Bulk Edit Notes");
            return View(vm);
        }
        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkEditNotes(BulkEditNotesViewModel model)
        {
            if (!ModelState.IsValid)
            {
                // Re-populate dropdown lists with selected values and keep dates intact
                string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null) return Unauthorized();

                var allLocations = _dbContext.Locations.Where(l => l.UserId == userId);

                // Countries
                var countries = await allLocations
                    .Select(l => l.Country!)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync();
                model.Countries = countries
                    .Select(c => new SelectListItem(c, c, c == model.Country))
                    .ToList();

                // Regions *within* the selected country
                var regionsQuery = allLocations;
                if (!string.IsNullOrEmpty(model.Country))
                    regionsQuery = regionsQuery.Where(l => l.Country == model.Country);
                var regions = await regionsQuery
                    .Select(l => l.Region!)
                    .Distinct()
                    .OrderBy(r => r)
                    .ToListAsync();
                model.Regions = regions
                    .Select(r => new SelectListItem(r, r, r == model.Region))
                    .ToList();

                // Places *within* the selected country+region
                var placesQuery = allLocations;
                if (!string.IsNullOrEmpty(model.Country))
                    placesQuery = placesQuery.Where(l => l.Country == model.Country);
                if (!string.IsNullOrEmpty(model.Region))
                    placesQuery = placesQuery.Where(l => l.Region == model.Region);
                var places = await placesQuery
                    .Select(l => l.Place!)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToListAsync();
                model.Places = places
                    .Select(p => new SelectListItem(p, p, p == model.Place))
                    .ToList();

                return View(model);
            }

            if (model.OperationScope != "bulk") return StatusCode(428, new { code = "protectedMutationRequired" });
            // Deliberate bulk uses Apply-time membership and final locked Notes.
            string? userId2 = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId2 == null) return Unauthorized();

            var query = _dbContext.Locations
                .Where(l => l.UserId == userId2);

            if (!string.IsNullOrEmpty(model.Country))
                query = query.Where(l => l.Country == model.Country);
            if (!string.IsNullOrEmpty(model.Region))
                query = query.Where(l => l.Region == model.Region);
            if (!string.IsNullOrEmpty(model.Place))
                query = query.Where(l => l.Place == model.Place);

            if (model.FromDate.HasValue)
            {
                var fromDate = DateTime.SpecifyKind(model.FromDate.Value.Date, DateTimeKind.Utc);
                query = query.Where(l => l.LocalTimestamp >= fromDate);
            }

            if (model.ToDate.HasValue)
            {
                var toDate = DateTime.SpecifyKind(model.ToDate.Value.Date.AddDays(1), DateTimeKind.Utc);
                query = query.Where(l => l.LocalTimestamp < toDate);
            }

            await using var bulkTransaction = await LocationDeletionFence.BeginAsync(_dbContext, userId2, HttpContext.RequestAborted);
            // SQL predicate and FOR UPDATE share one statement snapshot; EvalPlanQual rechecks after waits.
            var from = model.FromDate.HasValue ? DateTime.SpecifyKind(model.FromDate.Value.Date, DateTimeKind.Utc) : (DateTime?)null;
            var through = model.ToDate.HasValue ? DateTime.SpecifyKind(model.ToDate.Value.Date.AddDays(1), DateTimeKind.Utc) : (DateTime?)null;
            var list = _dbContext.Database.IsNpgsql()
                ? await _dbContext.Locations.FromSqlInterpolated($$"""
                    SELECT * FROM "Locations" WHERE "UserId" = {{userId2}}
                    AND ({{string.IsNullOrEmpty(model.Country)}} OR "Country" = {{model.Country}})
                    AND ({{string.IsNullOrEmpty(model.Region)}} OR "Region" = {{model.Region}})
                    AND ({{string.IsNullOrEmpty(model.Place)}} OR "Place" = {{model.Place}})
                    AND ({{from}}::timestamptz IS NULL OR "LocalTimestamp" >= {{from}})
                    AND ({{through}}::timestamptz IS NULL OR "LocalTimestamp" < {{through}})
                    ORDER BY "Id" FOR UPDATE
                    """).ToListAsync(HttpContext.RequestAborted)
                : await query.OrderBy(row => row.Id).ToListAsync(HttpContext.RequestAborted);
            model.AffectedCount = list.Count;

            foreach (var loc in list)
            {
                if (model.ClearNotes)
                {
                    loc.Notes = null;
                }
                else if (model.Append)
                {
                    if (!string.IsNullOrWhiteSpace(model.Notes))
                        loc.Notes = RichNotes.Normalize((loc.Notes ?? "") + model.Notes!);
                }
                else
                {
                    loc.Notes = string.IsNullOrWhiteSpace(model.Notes)
                        ? null
                        : RichNotes.Normalize(model.Notes);
                }
            }

            await _dbContext.SaveChangesAsync();
            if (bulkTransaction != null) await bulkTransaction.CommitAsync(HttpContext.RequestAborted);

            SetAlert($"{model.AffectedCount} location(s) updated.", "success");

            var allLocations2 = _dbContext.Locations.Where(l => l.UserId == userId2);

            // Countries
            var countries2 = await allLocations2
                .Select(l => l.Country!)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();
            model.Countries = countries2
                .Select(c => new SelectListItem(c, c, c == model.Country))
                .ToList();

            // Regions *within* the selected country
            var regionsQuery2 = allLocations2;
            if (!string.IsNullOrEmpty(model.Country))
                regionsQuery2 = regionsQuery2.Where(l => l.Country == model.Country);
            var regions2 = await regionsQuery2
                .Select(l => l.Region!)
                .Distinct()
                .OrderBy(r => r)
                .ToListAsync();
            model.Regions = regions2
                .Select(r => new SelectListItem(r, r, r == model.Region))
                .ToList();

            // Places *within* the selected country+region
            var placesQuery2 = allLocations2;
            if (!string.IsNullOrEmpty(model.Country))
                placesQuery2 = placesQuery2.Where(l => l.Country == model.Country);
            if (!string.IsNullOrEmpty(model.Region))
                placesQuery2 = placesQuery2.Where(l => l.Region == model.Region);
            var places2 = await placesQuery2
                .Select(l => l.Place!)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();
            model.Places = places2
                .Select(p => new SelectListItem(p, p, p == model.Place))
                .ToList();

            return View(model);
        }


        [HttpGet]
        public async Task<JsonResult> GetRegions(string country)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Json(new List<string>());

            var regions = await _dbContext.Locations
                .Where(l => l.UserId == userId && l.Country == country)
                .Select(l => l.Region!)
                .Distinct()
                .OrderBy(r => r)
                .ToListAsync();
            return Json(regions);
        }

        [HttpGet]
        public async Task<JsonResult> GetPlaces(string country, string region)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Json(new List<string>());

            var places = await _dbContext.Locations
                .Where(l => l.UserId == userId
                            && l.Country == country
                            && l.Region == region)
                .Select(l => l.Place!)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();
            return Json(places);
        }

        /// <summary>
        /// Preview how many rows match the current filter (no update)
        /// </summary>
        [HttpGet]
        public async Task<JsonResult> PreviewCount(
            string? country, string? region, string? place,
            DateTime? fromDate, DateTime? toDate)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Json(new { count = 0 });

            if (fromDate.HasValue)
                fromDate = DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Utc);

            if (toDate.HasValue)
            {
                // Exclusive end boundary: less than next day
                toDate = DateTime.SpecifyKind(toDate.Value.Date.AddDays(1), DateTimeKind.Utc);
            }


            var q = _dbContext.Locations.Where(l => l.UserId == userId);

            if (!string.IsNullOrEmpty(country)) q = q.Where(l => l.Country == country);
            if (!string.IsNullOrEmpty(region)) q = q.Where(l => l.Region == region);
            if (!string.IsNullOrEmpty(place)) q = q.Where(l => l.Place == place);

            if (fromDate.HasValue)
                q = q.Where(l => l.LocalTimestamp >= fromDate.Value);

            if (toDate.HasValue)
                q = q.Where(l => l.LocalTimestamp < toDate.Value);


            int count = await q.CountAsync();
            return Json(count);
        }
    }
}
