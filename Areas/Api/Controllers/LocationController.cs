using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Util;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Areas.Api.Controllers
{
    [Area("Api")]
    [Route("api/[controller]")]
    [ApiController]
    public class LocationController : BaseApiController
    {
        private readonly IMemoryCache _cache;
        private readonly IApplicationSettingsService _settingsService;
        private readonly ReverseGeocodingService _reverseGeocodingService;
        private readonly LocationService _locationService;
        private readonly SseService _sse;
        private readonly ILocationStatsService _statsService;

        // Constants for default threshold settings
        private const int DefaultLocationTimeThresholdMinutes = 5; // Default to 5 minutes
        private const double DefaultLocationDistanceThresholdMeters = 15; // Default to 15 meters

        public LocationController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger,
            IMemoryCache cache, IApplicationSettingsService settingsService,
            ReverseGeocodingService reverseGeocodingService, LocationService locationService, SseService sse,
            ILocationStatsService statsService)
            : base(dbContext, logger)
        {
            _cache = cache;
            _settingsService = settingsService;
            _reverseGeocodingService = reverseGeocodingService;
            _locationService = locationService;
            _sse = sse;
            _statsService = statsService;
        }

        [HttpPost]
        [Route("/api/location/log-location")]
        public async Task<IActionResult> LogLocation([FromBody] GpsLoggerLocationDto dto)
        {
            var requestId = Guid.NewGuid();
            _logger.LogInformation($"Lat: {dto.Latitude}, Long: {dto.Longitude}, Accuracy: {dto.Accuracy}, Speed: {dto.Speed}, Altitude: {dto.Altitude}");

            ApplicationUser? user = GetUserFromToken();
            if (user == null)
                return Unauthorized("Invalid or missing API token.");

            if (!user.IsActive)
                return Forbid("User is not active.");

            using (_logger.BeginScope(
                       new Dictionary<string, object> { ["RequestId"] = requestId, ["UserId"] = user.Id }))
            {
                _logger.LogInformation("Received location update request.");

                if (dto == null || (dto.Latitude == 0 && dto.Longitude == 0))
                {
                    _logger.LogWarning("Invalid location data received.");
                    return BadRequest("Location data is invalid.");
                }

                if (dto.Latitude < -90 || dto.Latitude > 90 || dto.Longitude < -180 || dto.Longitude > 180)
                {
                    _logger.LogWarning("Out-of-range coordinates: {Latitude}, {Longitude}", dto.Latitude,
                        dto.Longitude);
                    return BadRequest("Latitude or Longitude is out of range.");
                }

                ApplicationSettings? settings = _settingsService.GetSettings();
                int locationTimeThreshold =
                    settings?.LocationTimeThresholdMinutes ?? DefaultLocationTimeThresholdMinutes;
                double locationDistanceThreshold =
                    settings?.LocationDistanceThresholdMeters ?? DefaultLocationDistanceThresholdMeters;

                DateTime utcTimestamp;
                string timeZoneId;
                try
                {
                    timeZoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(dto.Latitude, dto.Longitude);
                    utcTimestamp = CoordinateTimeZoneConverter.ConvertToUtc(dto.Latitude, dto.Longitude, dto.Timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to convert local time to UTC for coordinates {Lat}, {Lon}",
                        dto.Latitude, dto.Longitude);
                    return StatusCode(500, "Failed to process timestamp and timezone.");
                }

                Point coordinates = new Point(dto.Longitude, dto.Latitude) { SRID = 4326 };

                string cacheKey = $"lastLocation_{user.Id}";
                if (!_cache.TryGetValue(cacheKey, out Location? lastLocation))
                {
                    lastLocation = _dbContext.Locations
                        .Where(l => l.UserId == user.Id)
                        .OrderByDescending(l => l.Timestamp)
                        .FirstOrDefault();

                    if (lastLocation != null)
                        _cache.Set(cacheKey, lastLocation, TimeSpan.FromMinutes(locationTimeThreshold));
                }

                if (lastLocation != null)
                {
                    var timeDifference = utcTimestamp - lastLocation.Timestamp;
                    if (timeDifference.TotalMinutes < locationTimeThreshold)
                    {
                        _logger.LogInformation("Location skipped due to time threshold. TimeDifference: {TimeDiff} mins",
                            timeDifference.TotalMinutes);
                        return Ok(new { Message = "Location skipped. Time threshold not met." });
                    }

                    double distanceDifference = coordinates.Distance(lastLocation.Coordinates);
                    if (distanceDifference < locationDistanceThreshold)
                    {
                        _logger.LogInformation(
                            "Location skipped due to distance threshold. DistanceDifference: {DistanceDiff} meters",
                            distanceDifference);
                        return Ok(new { Message = "Location skipped. Distance threshold not met." });
                    }
                }

                var location = new Location
                {
                    UserId = user.Id,
                    Timestamp = utcTimestamp,
                    LocalTimestamp = dto.Timestamp,
                    TimeZoneId = timeZoneId,
                    Coordinates = coordinates,
                    Accuracy = dto.Accuracy,
                    Altitude = dto.Altitude,
                    Speed = dto.Speed,
                    LocationType = dto.LocationType,
                    Notes = dto.Notes,
                    ActivityTypeId = dto.ActivityTypeId,
                    VehicleId = dto.VehicleId
                };

                try
                {
                    var apiToken = user.ApiTokens.FirstOrDefault(t => t.Name == "Mapbox");
                    if (apiToken != null)
                    {
                        var locationInfo = await _reverseGeocodingService.GetReverseGeocodingDataAsync(
                            dto.Latitude, dto.Longitude, apiToken.Token, apiToken.Name);

                        location.FullAddress = locationInfo.FullAddress;
                        location.Address = locationInfo.Address;
                        location.AddressNumber = locationInfo.AddressNumber;
                        location.StreetName = locationInfo.StreetName;
                        location.PostCode = locationInfo.PostCode;
                        location.Place = locationInfo.Place;
                        location.Region = locationInfo.Region;
                        location.Country = locationInfo.Country;
                    }

                    _dbContext.Locations.Add(location);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation("Location saved with ID {LocationId} at {Timestamp}", location.Id,
                        location.Timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save location for user.");
                    return StatusCode(500, "Internal server error while saving location.");
                }

                _cache.Set(cacheKey, location, TimeSpan.FromMinutes(locationTimeThreshold));

                await _sse.BroadcastAsync($"location-update-{user.UserName}", JsonSerializer.Serialize(new
                {
                    LocationId = location.Id,
                    TimeStamp = location.Timestamp,
                }));

                return Ok(new { Message = "Location logged successfully", Location = location });
            }
        }


        /// <summary>
        /// Gets user's locations filtered by zoom and map bounds.
        /// As the zoom level increases, more records will be returned up to 10.000.
        /// </summary>
        /// <param name="minLongitude"></param>
        /// <param name="minLatitude"></param>
        /// <param name="maxLongitude"></param>
        /// <param name="maxLatitude"></param>
        /// <param name="zoomLevel"></param>
        /// <returns></returns>
        [Authorize]
        [HttpPost("get-user-locations")]
        public async Task<IActionResult> GetUserLocations([FromBody] LocationFilterRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { Success = false, Message = "Invalid request payload." });
            }

            // Check if the user is authenticated
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized(new { Success = false, Message = "User is not authenticated." });
            }

            string userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                return BadRequest(new { Success = false, Message = "Invalid user identifier." });
            }

            try
            {
                // 1) fetch location DTOs
                var (locationDtos, totalItems) = await _locationService.GetLocationsAsync(
                    request.MinLongitude,
                    request.MinLatitude,
                    request.MaxLongitude,
                    request.MaxLatitude,
                    request.ZoomLevel,
                    userId,
                    CancellationToken.None
                );

                // 2) materialize into a list so we can enumerate twice
                var locationList = locationDtos.ToList();

                // 3) compute each point’s UTC‐instant and find the max
                var latestLocationId = locationList
                    .Select(loc => new
                    {
                        loc.Id,
                        Utc = CoordinateTimeZoneConverter.ConvertToUtc(
                            latitude: loc.Coordinates.Y,
                            longitude: loc.Coordinates.X,
                            localDateTime: loc.LocalTimestamp
                        )
                    })
                    .OrderByDescending(x => x.Utc)
                    .FirstOrDefault()?.Id;

                // 4) project and flag using that Id
                var result = locationList.Select(location => new PublicLocationDto()
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

                    // true if this was the most recent event in *absolute* time
                    IsLatestLocation = location.Id == latestLocationId,

                    LocationTimeThresholdMinutes = location.LocationTimeThresholdMinutes
                });

                return Ok(new
                {
                    Success = true,
                    Data = result,
                    TotalItems = totalItems,
                    CurrentPage = 1, // Modify as needed for pagination
                    PageSize = locationDtos.Count
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error in getting user locations.\n{e.Message}");
                return Ok(new
                {
                    Success = true,
                    Data = $"{e}",
                    TotalItems = string.Empty,
                    CurrentPage = 1, // Modify as needed for pagination
                    PageSize = 1
                });
            }
        }


        /// <summary>
        /// Delete locations
        /// </summary>
        public class BulkDeleteRequest
        {
            public List<int>? LocationIds { get; set; }
        }

        /// <summary>
        /// Bulk delete location objects
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("bulk-delete")]
        public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest request)
        {
            // Ensure there are location IDs provided
            if (request?.LocationIds == null || !request.LocationIds.Any())
            {
                return BadRequest(new { success = false, message = "No location IDs provided." });
            }

            // Get the currently logged-in user's ID
            var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Unauthorized(new { success = false, message = "Unauthorized." });
            }

            try
            {
                // Find the locations in the database that match the provided IDs that belong to the current user
                List<Location> locationsToDelete = await _dbContext.Locations
                    .Where(location => request.LocationIds.Contains(location.Id) && location.UserId == currentUserId)
                    .ToListAsync();

                // If no matching locations are found
                if (locationsToDelete.Count == 0)
                {
                    return NotFound(new { success = false, message = "No locations found for deletion." });
                }

                // Delete the locations
                _dbContext.Locations.RemoveRange(locationsToDelete);
                await _dbContext.SaveChangesAsync();

                return Ok(new
                    { success = true, message = $"{locationsToDelete.Count} locations deleted successfully." });
            }
            catch (Exception ex)
            {
                // Log the exception (optional) and return a server error
                Console.Error.WriteLine(ex);
                _logger.LogError(ex, $"Error in bulk-delete: {ex.Message}");
                return StatusCode(500,
                    new { success = false, message = "An error occurred while deleting the locations." });
            }
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search(
            string? userId,
            DateTime? fromTimestamp,
            DateTime? toTimestamp,
            string? locationType,
            string? activity, // Changed from activityTypeId to activity (name)
            string? notes,
            string? address, // Add address parameter
            string? country,
            string? region,
            string? place,
            int page = 1,
            int pageSize = 10)
        {
            // Force userId to be the currently logged-in user's ID
            var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(currentUserId))
            {
                return Unauthorized(); // User not authenticated
            }

            // Always override userId with current user's ID
            userId = currentUserId;

            IQueryable<Location> query = _dbContext.Locations
                .Include(l => l.ActivityType) // Include ActivityType for filtering by name
                .AsNoTracking(); // Disable tracking for performance

            // Only filter by the current user's ID
            query = query.Where(l => l.UserId == userId);

            // Apply rest filters
            if (!string.IsNullOrEmpty(userId))
            {
                query = query.Where(l => l.UserId == userId);
            }

            // Handle fromTimestamp as a date-only filter
            if (fromTimestamp.HasValue)
            {
                DateTime fromDateUtc = fromTimestamp.Value.Date.ToUniversalTime(); // Convert to UTC, truncating time
                query = query.Where(l => l.LocalTimestamp >= fromDateUtc);
            }

            // Handle toTimestamp as a date-only filter
            if (toTimestamp.HasValue)
            {
                // Convert to UTC, including the full day (end of the day)
                DateTime toDateUtc = toTimestamp.Value.Date.AddDays(1).AddSeconds(-1).ToUniversalTime();
                query = query.Where(l => l.LocalTimestamp <= toDateUtc);
            }

            if (!string.IsNullOrEmpty(locationType))
            {
                query = query.Where(l => l.LocationType == locationType);
            }

            if (!string.IsNullOrEmpty(activity)) // Filter by ActivityType.Name
            {
                query = query.Where(l => l.ActivityType.Name.ToLower() == activity.ToLower());
            }

            // Initially, there was a geofence design in app but for now it will not be implemented
            // if (!string.IsNullOrEmpty(geofenceName))
            // {
            //     query = query.Where(l => l.GeofenceName.ToLower() == geofenceName.ToLower());
            // }
            //
            // if (isInsideGeofence.HasValue)
            // {
            //     query = query.Where(l => l.IsInsideGeofence == isInsideGeofence.Value);
            // }

            if (!string.IsNullOrEmpty(notes))
            {
                query = query.Where(l => l.Notes.ToLower().Contains(notes.ToLower()));
            }

            // Apply address filter
            if (!string.IsNullOrEmpty(address))
            {
                query = query.Where(l => l.Address.ToLower().Contains(address.ToLower()));
            }

            // Apply Country filter
            if (!string.IsNullOrEmpty(country))
            {
                query = query.Where(l => l.Country.ToLower().Contains(country.ToLower()));
            }

            // Apply Region filter
            if (!string.IsNullOrEmpty(region))
            {
                query = query.Where(l => l.Region.ToLower().Contains(region.ToLower()));
            }

            // Apply City filter (Place in reverse geocoding terms)
            if (!string.IsNullOrEmpty(place))
            {
                query = query.Where(l => l.Place.ToLower().Contains(place.ToLower()));
            }

            // Apply pagination
            int skip = (page - 1) * pageSize;
            List<Location> locations = await query.Skip(skip).Take(pageSize).ToListAsync();
            // Get the total number of items
            int totalItems = await query.CountAsync();

            // Handle empty data
            return locations == null || locations.Count == 0
                ? Ok(new
                {
                    Success = true,
                    Data = new List<object>(), // Return an empty array
                    TotalItems = totalItems,
                    CurrentPage = page,
                    PageSize = pageSize
                })
                : (IActionResult)Ok(new
                {
                    Success = true,
                    Data = locations.Select(l => new
                    {
                        l.Id,
                        Coordinates = new
                        {
                            Longitude = l.Coordinates?.X,
                            Latitude = l.Coordinates?.Y
                        },
                        LocalTimestamp =
                            CoordinateTimeZoneConverter.ConvertUtcToLocal(l.Coordinates.Y, l.Coordinates.X,
                                l.LocalTimestamp),
                        l.TimeZoneId,
                        Activity = l.ActivityType?.Name,
                        l.Address,
                        l.Country,
                        l.Place,
                        l.Region,
                        l.FullAddress,
                        l.PostCode,
                        l.AddressNumber,
                        l.Notes,
                        l.Altitude
                    }).ToList(),
                    TotalItems = totalItems,
                    CurrentPage = page,
                    PageSize = pageSize
                });
        }

        /// <summary>
        /// Calculates User x Location stats
        /// </summary>
        /// <returns></returns>
        [HttpGet("stats")]
        public async Task<ActionResult<UserLocationStatsDto>> GetStats()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Unauthorized();

            var stats = await _statsService.GetStatsForUserAsync(userId);
            return Ok(stats);
        }
    }
}