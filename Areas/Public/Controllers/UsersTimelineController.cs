using Wayfarer.Util;
﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
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

            if (user == null || !PublicTimelineEligibilityResolver.Resolve(user).IsEffectivelyPublic)
            {
                return NotFound("User not found or timeline is not public.");
            }

            ViewData["Username"] = user.UserName;
            ViewData["TimelineLive"] = PublicTimelineEligibilityResolver.Resolve(user).IsLive;
            ViewData["TimelineTitle"] = user.ResolveTimelineTitle();
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

            if (user == null || !PublicTimelineEligibilityResolver.Resolve(user).IsEffectivelyPublic)
            {
                return NotFound("User not found or timeline is not public.");
            }

            ViewData["Username"] = user.UserName;
            ViewData["TimelineLive"] = PublicTimelineEligibilityResolver.Resolve(user).IsLive;
            ViewData["TimelineTitle"] = user.ResolveTimelineTitle();
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
            var projection = user is null ? null : new PublicTimelineLocationProjection(user, DateTime.UtcNow);
            if (projection is null || !projection.IsAvailable)
            {
                return NotFound("User not found or timeline is not public.");
            }

            // Latest selection shares eligibility and remains independent of viewport and zoom.
            var latestLocation = await projection.Query(_dbContext)
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
                    user!.Id,
                    CancellationToken.None,
                    projection
                );

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
                    ProviderAddressLine1 = location.ProviderAddressLine1,
                    AddressNumber = location.AddressNumber,
                    IsGeoapifyAddress = location.IsGeoapifyAddress,
                    StreetName = location.StreetName,
                    PostCode = location.PostCode,
                    Place = location.Place,
                    Region = location.Region,
                    Country = location.Country,
                    ResolvedFeatureName = location.ResolvedFeatureName,
                    ResolvedFeatureType = location.ResolvedFeatureType,
                    Notes = RichNotes.NormalizeForPersistence(location.Notes),

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

            var projection = user is null ? null : new PublicTimelineLocationProjection(user, DateTime.UtcNow);
            if (projection is null || !projection.IsAvailable)
            {
                return NotFound("User not found or timeline is not public.");
            }


            // Aggregate eligible history independently of viewport sampling.
            var statsDto = await _statsService.GetPublicStatsAsync(projection);

            return Ok(statsDto);
        }
    }
}
