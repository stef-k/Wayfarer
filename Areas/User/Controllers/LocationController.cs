using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
    public class LocationController : BaseController
    {
        private readonly ReverseGeocodingService _reverseGeocodingService;

        public LocationController(ILogger<BaseController> logger, ApplicationDbContext dbContext,
            ReverseGeocodingService reverseGeocodingService)
            : base(logger, dbContext)
        {
            _reverseGeocodingService = reverseGeocodingService;
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
                        Notes = model.Notes,
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
                ApplicationUser? user = await _dbContext.ApplicationUsers
                    .Include(u => u.ApiTokens)
                    .FirstOrDefaultAsync(u => u.Id == model.UserId);
                ApiToken? apiToken = user.ApiTokens.Where(t => t.Name == "Mapbox").FirstOrDefault();
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
                    ActivityTypeId = model.SelectedActivityId, // Use the selected activity ID
                    Accuracy = model.Accuracy,
                    Altitude = model.Altitude,
                    Speed = model.Speed,
                    Address = model.Address,
                    Notes = model.Notes
                };


                if (apiToken != null)
                {
                    ReverseLocationResults locationInfo =
                        await _reverseGeocodingService.GetReverseGeocodingDataAsync(location.Coordinates.Y,
                            location.Coordinates.X, apiToken.Token, apiToken.Name);

                    location.FullAddress = locationInfo.FullAddress;
                    location.Address = locationInfo.Address;
                    location.AddressNumber = locationInfo.AddressNumber;
                    location.StreetName = locationInfo.StreetName;
                    location.PostCode = locationInfo.PostCode;
                    location.Place = locationInfo.Place;
                    location.Region = locationInfo.Region;
                    location.Country = locationInfo.Country;
                }

                // Save the location to the database
                _dbContext.Locations.Add(location);
                await _dbContext.SaveChangesAsync();

                LogAction("CreateLocation", $"Location added for user {model.UserId}");
                SetAlert("The location has been successfully created.", "success");
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
        public async Task<IActionResult> Edit(int id)
        {
            string userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _logger.LogInformation("Edit[GET] start for LocationId={Id}, UserId={User}", id, userId);

            try
            {
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
                    Notes = location.Notes,
                    FullAddress = location.FullAddress,
                    AddressNumber = location.AddressNumber,
                    StreetName = location.StreetName,
                    PostCode = location.PostCode,
                    Place = location.Place,
                    Region = location.Region,
                    Country = location.Country,
                };

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
        public async Task<IActionResult> Edit(AddLocationViewModel model)
        {
            if (!ModelState.IsValid)
            {
                // Reload activity types and return the view with errors
                List<ActivityType> activityTypes = await _dbContext.ActivityTypes.ToListAsync();
                model.ActivityTypes = activityTypes.Select(a => new SelectListItem
                {
                    Value = a.Id.ToString(),
                    Text = a.Name,
                    Selected = a.Id == model.SelectedActivityId
                }).ToList();

                SetPageTitle("Edit Location");
                return View(model);
            }

            // Retrieve the existing location entity
            Location? location = await _dbContext.Locations.FindAsync(model.Id);
            if (location == null || location.UserId != User.FindFirstValue(ClaimTypes.NameIdentifier))
            {
                SetAlert("Location not found or you don't have permission to edit it.", "danger");
                return RedirectToAction("Index");
            }

            // Update the location fields
            var utc = CoordinateTimeZoneConverter.ConvertToUtc(model.Latitude, model.Longitude, model.LocalTimestamp);
            location.Coordinates.X = model.Longitude;
            location.Coordinates.Y = model.Latitude;
            location.Altitude = model.Altitude;
            location.Address = model.Address;
            location.LocalTimestamp = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            location.TimeZoneId =
                CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(model.Latitude, model.Longitude);
            location.ActivityTypeId = model.SelectedActivityId;
            location.Notes = model.Notes;

            model.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ApplicationUser? user = await _dbContext.ApplicationUsers
                .Include(u => u.ApiTokens)
                .FirstOrDefaultAsync(u => u.Id == model.UserId);
            ApiToken? apiToken = user.ApiTokens.Where(t => t.Name == "Mapbox").FirstOrDefault();

            if (apiToken != null)
            {
                ReverseLocationResults locationInfo =
                    await _reverseGeocodingService.GetReverseGeocodingDataAsync(location.Coordinates.Y,
                        location.Coordinates.X, apiToken.Token, apiToken.Name);

                location.FullAddress = locationInfo.FullAddress;
                location.Address = locationInfo.Address;
                location.AddressNumber = locationInfo.AddressNumber;
                location.StreetName = locationInfo.StreetName;
                location.PostCode = locationInfo.PostCode;
                location.Place = locationInfo.Place;
                location.Region = locationInfo.Region;
                location.Country = locationInfo.Country;
            }

            await _dbContext.SaveChangesAsync();

            SetAlert("Location updated successfully.", "success");
            return RedirectToAction("Index");
        }
    }
}