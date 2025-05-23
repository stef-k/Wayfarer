using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Public.Controllers
{
    [Area("Public")]
    public class UsersTimelineController : BaseController
    {
        private readonly LocationService _locationService;
        private readonly ILocationStatsService _statsService;

        public UsersTimelineController(ILogger<BaseController> logger, ApplicationDbContext dbContext,
            LocationService locationService, ILocationStatsService statsService) : base(logger,
            dbContext)
        {
            _locationService = locationService;
            _statsService = statsService;
        }

        /// <summary>
        /// Returns the view page for user's public timeline
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        //[HttpGet]
        [Route("Public/Users/Timeline/{username}")]
        public async Task<IActionResult> Index(string username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return BadRequest("Username is required.");
            }

            ApplicationUser? user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserName == username);

            if (user == null || !user.IsTimelinePublic)
            {
                return NotFound("User not found or timeline is not public.");
            }

            ViewData["Username"] = user.UserName;
            ViewData["TimelineLive"] = user.PublicTimelineTimeThreshold == "now";
            if (!string.IsNullOrEmpty(user.DisplayName))
            {
                ViewData["DisplayName"] = user.DisplayName;
                SetPageTitle($"{user.DisplayName} Timeline");
            }
            else
            {
                ViewData["DisplayName"] = user.UserName;
                SetPageTitle($"{user.UserName} Timeline");
            }

            return View("Timeline");
        }

        /// <summary>
        /// Returns the view page for user's public timeline
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        //[HttpGet]
        [Route("Public/Users/Timeline/{username}/embed")]
        public async Task<IActionResult> Embed(string username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return BadRequest("Username is required.");
            }

            ApplicationUser? user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserName == username);

            if (user == null || !user.IsTimelinePublic)
            {
                return NotFound("User not found or timeline is not public.");
            }

            ViewData["Username"] = user.UserName;
            ViewData["TimelineLive"] = user.PublicTimelineTimeThreshold == "now";
            if (!string.IsNullOrEmpty(user.DisplayName))
            {
                ViewData["DisplayName"] = user.DisplayName;
                SetPageTitle($"{user.DisplayName} Timeline");
            }
            else
            {
                ViewData["DisplayName"] = user.UserName;
                SetPageTitle($"{user.UserName} Timeline");
            }

            return View("Embed");
        }

        /// <summary>
        /// Gets all of user's locations, based on user's settings.
        /// The location should be set to public by the user and there must be a time threshold up to what
        /// point in time from current date time the user wants to show his locations.
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("Public/Users/GetPublicTimeline")]
        public async Task<IActionResult> GetPublicTimeline([FromBody] LocationFilterRequest request)
        {
            // keep the checks both in view controller and here
            ApplicationUser? user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserName == request.Username);
            if (user == null || !user.IsTimelinePublic)
            {
                return NotFound("User not found or timeline is not public.");
            }

            string? threshold = user.PublicTimelineTimeThreshold;
            threshold ??= "now";
            TimeSpan timeSpan = Util.TimespanHelper.ParseTimeThreshold(threshold);
            DateTime cutOffTime = DateTime.UtcNow.Subtract(timeSpan);

            // get the latest location
            var latestLocation = await _dbContext.Locations
                .Where(l => l.UserId == user.Id && l.LocalTimestamp <= cutOffTime)
                .Include(l => l.ActivityType)
                .OrderByDescending(l => l.LocalTimestamp)
                .FirstOrDefaultAsync();

            try
            {
                var (locationDtos, totalItems) = await _locationService.GetLocationsAsync(
                    request.MinLongitude,
                    request.MinLatitude,
                    request.MaxLongitude,
                    request.MaxLatitude,
                    request.ZoomLevel,
                    user.Id,
                    CancellationToken.None
                );

                var settings = await _dbContext.ApplicationSettings.FirstOrDefaultAsync();

                // check against "now" threshold
                if (threshold != "now")
                {
                    locationDtos = locationDtos.Where(l => l.LocalTimestamp <= cutOffTime).ToList();
                }

                // Load all hidden areas for this user
                var hiddenAreas = await _dbContext.HiddenAreas
                    .Where(h => h.UserId == user.Id)
                    .ToListAsync();

                // Filter out any locations that fall inside any of the user's hidden areas
                locationDtos = locationDtos
                    .Where(loc =>
                        !hiddenAreas.Any(area => area.Area != null && area.Area.Contains(loc.Coordinates))
                    )
                    .ToList();

                var result = locationDtos.Select(location => new PublicLocationDto()
                {
                    Id = location.Id,
                    Timestamp = location.Timestamp,
                    LocalTimestamp = location.LocalTimestamp,
                    Coordinates = location.Coordinates,
                    Timezone = location.Timezone,
                    Accuracy = location.Accuracy,
                    Altitude = location.Altitude,
                    Speed = location.Speed,
                    LocationType = location.LocationType,
                    ActivityType = location.ActivityType,
                    Address = location.Address,
                    FullAddress = location.FullAddress,
                    StreetName = location.StreetName,
                    PostCode = location.PostCode,
                    Place = location.Place,
                    Region = location.Region,
                    Country = location.Country,
                    GeofenceName = location.GeofenceName,
                    IsInsideGeofence = location.IsInsideGeofence,
                    GeofenceRadius = location.GeofenceRadius,
                    Notes = location.Notes,
                    VehicleId = location.VehicleId,

                    // User's latest location unrelated from filtered zoom & viewport but based on threshold
                    // the user has set for his public timeline.
                    IsLatestLocation = location.Id == latestLocation?.Id,
                    // set if is live location based on application's settings location logging settings
                    // the frontend will then compare local timestamp if current date time is <= to LocationTimeThresholdMinutes
                    // and set the realitime  marker.
                    LocationTimeThresholdMinutes = location.LocationTimeThresholdMinutes
                });

                return Ok(new
                {
                    Success = true,
                    Data = result,
                    TotalItems = result.Count(),
                    CurrentPage = 1, // Modify as needed for pagination
                    PageSize = result.Count()
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
                return Ok(new
                {
                    Success = false,
                    Data = $"Error: {e.Message}",
                    TotalItems = 0,
                    CurrentPage = 1,
                    PageSize = 0
                });
            }
        }


        /// <summary>
        /// Calculates User x Location stats
        /// </summary>
        /// <param name="username">User's unique username</param>
        /// <returns></returns>
        [HttpGet("Public/Users/GetPublicStats/{username}")]
        public async Task<IActionResult> GetPublicStats(string username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return BadRequest("Username is required.");
            }

            ApplicationUser? user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserName == username);

            if (user == null || !user.IsTimelinePublic)
            {
                return NotFound("User not found or timeline is not public.");
            }


            // 2) Delegate all the heavy‐lifting to your stats service
            var statsDto = await _statsService.GetStatsForUserAsync(user.Id);

            return Ok(statsDto);
        }
    }
}