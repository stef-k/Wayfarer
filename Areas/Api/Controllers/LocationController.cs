using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
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
        private readonly Parsers.LocationService _chronologicalLocationService;

        // Constants for default threshold settings
        private const int DefaultLocationTimeThresholdMinutes = 5; // Default to 5 minutes
        private const double DefaultLocationDistanceThresholdMeters = 15; // Default to 15 meters

        // Constants for check-in rate limiting
        private const int CheckInMinIntervalSeconds = 30; // Minimum 30 seconds between check-ins
        private const int CheckInMaxPerHour = 60; // Maximum 60 check-ins per hour per user

        public LocationController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger,
            IMemoryCache cache, IApplicationSettingsService settingsService,
            ReverseGeocodingService reverseGeocodingService, LocationService locationService, SseService sse,
            ILocationStatsService statsService, Parsers.LocationService chronologicalLocationService)
            : base(dbContext, logger)
        {
            _cache = cache;
            _settingsService = settingsService;
            _reverseGeocodingService = reverseGeocodingService;
            _locationService = locationService;
            _sse = sse;
            _statsService = statsService;
            _chronologicalLocationService = chronologicalLocationService;
        }

        /// <summary>
        /// Manual check-in endpoint for user-initiated location logging.
        /// This endpoint bypasses time/distance thresholds but includes rate limiting
        /// to prevent spam and abuse from rapid-fire button pressing or duplicate requests.
        /// </summary>
        /// <param name="dto">Location data for the check-in</param>
        /// <returns>Check-in result with success status and optional message</returns>
        [HttpPost]
        [Route("/api/location/check-in")]
        public async Task<IActionResult> CheckIn([FromBody] GpsLoggerLocationDto dto)
        {
            var requestId = Guid.NewGuid();
            _logger.LogInformation(
                $"CHECK-IN: Lat: {dto.Latitude}, Long: {dto.Longitude}, Accuracy: {dto.Accuracy}, Speed: {dto.Speed}, Altitude: {dto.Altitude}");

            ApplicationUser? user = GetUserFromToken();
            if (user == null)
                return Unauthorized("Invalid or missing API token.");

            if (!user.IsActive)
                return Forbid("User is not active.");

            using (_logger.BeginScope(
                       new Dictionary<string, object>
                           { ["RequestId"] = requestId, ["UserId"] = user.Id, ["RequestType"] = "CHECK_IN" }))
            {
                _logger.LogInformation("Received check-in request.");

                // Basic validation (same as log-location)
                if (dto == null || (dto.Latitude == 0 && dto.Longitude == 0))
                {
                    _logger.LogWarning("Invalid check-in location data received.");
                    return BadRequest("Check-in location data is invalid.");
                }

                if (dto.Latitude < -90 || dto.Latitude > 90 || dto.Longitude < -180 || dto.Longitude > 180)
                {
                    _logger.LogWarning("Out-of-range coordinates in check-in: {Latitude}, {Longitude}", dto.Latitude,
                        dto.Longitude);
                    return BadRequest("Latitude or Longitude is out of range.");
                }

                // Rate limiting for check-ins to prevent spam/abuse
                var rateLimitResult = await ValidateCheckInRateLimit(user.Id);
                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Check-in rate limit exceeded for user {UserId}: {Reason}", user.Id,
                        rateLimitResult.Reason);
                    return TooManyRequests(rateLimitResult.Reason);
                }

                // Process timestamp and timezone (same as log-location)
                DateTime utcTimestamp;
                string timeZoneId;
                try
                {
                    timeZoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(
                        dto.Latitude, dto.Longitude);

                    // Convert to UTC if not already
                    if (dto.Timestamp.Kind == DateTimeKind.Utc)
                    {
                        utcTimestamp = dto.Timestamp;
                    }
                    else
                    {
                        utcTimestamp = CoordinateTimeZoneConverter.ConvertToUtc(
                            dto.Latitude, dto.Longitude, dto.Timestamp);
                    }

                    // Ensure UTC marking for EF/SQL
                    utcTimestamp = DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to convert local time to UTC for check-in coordinates {Lat}, {Lon}",
                        dto.Latitude, dto.Longitude);
                    return StatusCode(500, "Failed to process timestamp and timezone for check-in.");
                }

                // Create coordinates (same as log-location)
                Point coordinates = new Point(dto.Longitude, dto.Latitude) { SRID = 4326 };

                // Create location record (same structure as log-location)
                var location = new Location
                {
                    UserId = user.Id,
                    Timestamp = DateTime.UtcNow, // Server timestamp (same as log-location)
                    LocalTimestamp = utcTimestamp, // Client timestamp converted to UTC (same as log-location)
                    TimeZoneId = timeZoneId,
                    Coordinates = coordinates,
                    Accuracy = dto.Accuracy,
                    Altitude = dto.Altitude,
                    Speed = dto.Speed,
                    LocationType = dto.LocationType ?? "Manual", // Default to Manual for check-ins
                    Notes = dto.Notes,
                    ActivityTypeId = dto.ActivityTypeId,
                    VehicleId = dto.VehicleId
                };

                try
                {
                    // Reverse geocoding (exactly like log-location)
                    var apiToken = user.ApiTokens.FirstOrDefault(t => t.Name == "Mapbox");
                    if (apiToken != null)
                    {
                        var locationInfo = await _reverseGeocodingService.GetReverseGeocodingDataAsync(
                            dto.Latitude, dto.Longitude, apiToken.Token, apiToken.Name);

                        _logger.LogInformation(
                            $"Check-in, user has mapbox Api token, we got reverse geocoding data: {locationInfo.FullAddress}");

                        location.FullAddress = locationInfo.FullAddress;
                        location.Address = locationInfo.Address;
                        location.AddressNumber = locationInfo.AddressNumber;
                        location.StreetName = locationInfo.StreetName;
                        location.PostCode = locationInfo.PostCode;
                        location.Place = locationInfo.Place;
                        location.Region = locationInfo.Region;
                        location.Country = locationInfo.Country;
                    }

                    // Save location (same as log-location)
                    _dbContext.Locations.Add(location);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation("Check-in location saved with ID {LocationId} at {Timestamp}", location.Id,
                        location.Timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save check-in location for user.");
                    return StatusCode(500, "Internal server error while saving check-in location.");
                }

                // Update cache with latest location (same as log-location)
                string cacheKey = $"lastLocation_{user.Id}";
                _cache.Set(cacheKey, location, TimeSpan.FromMinutes(30));

                // Update rate limiting tracking
                await UpdateCheckInRateTracking(user.Id);

                // SSE broadcast (same pattern as log-location and User/LocationController)
                await _sse.BroadcastAsync($"location-update-{user.UserName}", JsonSerializer.Serialize(new
                {
                    LocationId = location.Id,
                    TimeStamp = location.Timestamp,
                    Type = "check-in"
                }));

                // Note: Statistics are computed on-demand via GetStatsForUserAsync when needed

                // Return same format as log-location
                return Ok(new { Message = "Check-in logged successfully", Location = location });
            }
        }

        /// <summary>
        /// Validates rate limiting for check-in requests to prevent spam and abuse.
        /// Implements both time-based interval checking and hourly limits.
        /// </summary>
        /// <param name="userId">User ID to check rate limits for</param>
        /// <returns>Rate limit validation result</returns>
        private async Task<CheckInRateLimitResult> ValidateCheckInRateLimit(string userId)
        {
            // Check minimum interval between check-ins
            string lastCheckInCacheKey = $"lastCheckIn_{userId}";
            if (_cache.TryGetValue(lastCheckInCacheKey, out DateTime lastCheckInTime))
            {
                var timeSinceLastCheckIn = DateTime.UtcNow - lastCheckInTime;
                if (timeSinceLastCheckIn.TotalSeconds < CheckInMinIntervalSeconds)
                {
                    var remainingSeconds = CheckInMinIntervalSeconds - (int)timeSinceLastCheckIn.TotalSeconds;
                    return new CheckInRateLimitResult
                    {
                        IsAllowed = false,
                        Reason = $"Please wait {remainingSeconds} seconds between check-ins."
                    };
                }
            }

            // Check hourly limit
            string hourlyCacheKey = $"checkInCount_{userId}_{DateTime.UtcNow:yyyyMMddHH}";
            if (_cache.TryGetValue(hourlyCacheKey, out int currentHourlyCount))
            {
                if (currentHourlyCount >= CheckInMaxPerHour)
                {
                    return new CheckInRateLimitResult
                    {
                        IsAllowed = false,
                        Reason = $"Maximum {CheckInMaxPerHour} check-ins per hour exceeded. Please try again later."
                    };
                }
            }

            return new CheckInRateLimitResult { IsAllowed = true };
        }

        /// <summary>
        /// Updates rate limiting tracking after a successful check-in.
        /// </summary>
        /// <param name="userId">User ID to update tracking for</param>
        private async Task UpdateCheckInRateTracking(string userId)
        {
            // Update last check-in time
            string lastCheckInCacheKey = $"lastCheckIn_{userId}";
            _cache.Set(lastCheckInCacheKey, DateTime.UtcNow, TimeSpan.FromMinutes(5));

            // Update hourly count
            string hourlyCacheKey = $"checkInCount_{userId}_{DateTime.UtcNow:yyyyMMddHH}";
            if (_cache.TryGetValue(hourlyCacheKey, out int currentCount))
            {
                _cache.Set(hourlyCacheKey, currentCount + 1, TimeSpan.FromHours(1));
            }
            else
            {
                _cache.Set(hourlyCacheKey, 1, TimeSpan.FromHours(1));
            }
        }

        /// <summary>
        /// Returns a 429 Too Many Requests response with appropriate headers.
        /// </summary>
        /// <param name="message">Rate limit message to return</param>
        /// <returns>429 status code response</returns>
        private IActionResult TooManyRequests(string message)
        {
            Response.Headers["Retry-After"] = CheckInMinIntervalSeconds.ToString();
            return StatusCode(429, new { Message = message });
        }

        [HttpPost]
        [Route("/api/location/log-location")]
        public async Task<IActionResult> LogLocation([FromBody] GpsLoggerLocationDto dto)
        {
            var requestId = Guid.NewGuid();
            _logger.LogInformation(
                $"Lat: {dto.Latitude}, Long: {dto.Longitude}, Accuracy: {dto.Accuracy}, Speed: {dto.Speed}, Altitude: {dto.Altitude}");

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
                    timeZoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(
                        dto.Latitude, dto.Longitude);

                    // Only convert if the incoming timestamp isn’t already UTC
                    if (dto.Timestamp.Kind == DateTimeKind.Utc)
                    {
                        utcTimestamp = dto.Timestamp;
                    }
                    else
                    {
                        utcTimestamp = CoordinateTimeZoneConverter.ConvertToUtc(
                            dto.Latitude, dto.Longitude, dto.Timestamp);
                    }

                    // Ensure we mark this as UTC so EF/SQL stores it correctly
                    utcTimestamp = DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to convert local time to UTC for coordinates {Lat}, {Lon}",
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
                        _logger.LogInformation(
                            "Location skipped due to time threshold. TimeDifference: {TimeDiff} mins",
                            timeDifference.TotalMinutes);
                        return Ok(new { Message = "Location skipped. Time threshold not met." });
                    }

                    double distanceDifference = DistanceChecker.HaversineDistance(lastLocation.Coordinates.Y,
                        lastLocation.Coordinates.X,
                        dto.Latitude, dto.Longitude);

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
                    Timestamp = DateTime.UtcNow,
                    LocalTimestamp = utcTimestamp,
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

                        _logger.LogInformation(
                            $"Log-location, user has mapbox Api token, we got reverse geocoding data: {locationInfo.FullAddress}");

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
                    LocalTimestamp = DateTimeUtils.ConvertUtcToLocalTime(location.LocalTimestamp, location.Timezone),
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
            int pageSize = 20)
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
                .AsNoTracking()
                .Where(l => l.UserId == userId) // Only filter by the current user's ID
                .OrderByDescending(l => l.LocalTimestamp); // Disable tracking for performance


            // Apply rest filters
            if (!string.IsNullOrEmpty(userId))
            {
                query = query.Where(l => l.UserId == userId);
            }

            // Handle fromTimestamp as a date-only filter
            if (fromTimestamp.HasValue)
            {
                // Assume the fromTimestamp is UTC already from the frontend (e.g., React, JS)
                var from = DateTime.SpecifyKind(fromTimestamp.Value.Date, DateTimeKind.Utc);
                query = query.Where(l => l.LocalTimestamp >= from);
            }

            // Handle toTimestamp as a date-only filter
            if (toTimestamp.HasValue)
            {
                // Include the entire "to" day by going up to 23:59:59
                var to = DateTime.SpecifyKind(toTimestamp.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
                query = query.Where(l => l.LocalTimestamp <= to);
            }

            if (!string.IsNullOrEmpty(locationType))
            {
                query = query.Where(l => l.LocationType == locationType);
            }

            if (!string.IsNullOrWhiteSpace(activity)) // Filtering by ActivityType.Name
            {
                var loweredActivity = activity.ToLower();
                query = query.Where(l => l.ActivityType != null && l.ActivityType.Name.ToLower() == loweredActivity);
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                var loweredNotes = notes.ToLower();
                query = query.Where(l => l.Notes != null && l.Notes.ToLower().Contains(loweredNotes));
            }

            // Apply address filter
            if (!string.IsNullOrWhiteSpace(address))
            {
                var loweredAddress = address.ToLower();
                query = query.Where(l => l.Address != null && l.Address.ToLower().Contains(loweredAddress));
            }

            // Apply Country filter
            if (!string.IsNullOrWhiteSpace(country))
            {
                var lowered = country.ToLower();
                query = query.Where(l => l.Country != null && l.Country.ToLower().Contains(lowered));
            }


            // Apply Region filter
            if (!string.IsNullOrWhiteSpace(region))
            {
                var loweredRegion = region.ToLower();
                query = query.Where(l => l.Region != null && l.Region.ToLower().Contains(loweredRegion));
            }

            // Apply City filter (Place in reverse geocoding terms)
            if (!string.IsNullOrWhiteSpace(place))
            {
                var loweredPlace = place.ToLower();
                query = query.Where(l => l.Place != null && l.Place.ToLower().Contains(loweredPlace));
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
                    Data = locations
                        .Select(l => new
                        {
                            l.Id,
                            Coordinates = new
                            {
                                Longitude = l.Coordinates?.X,
                                Latitude = l.Coordinates?.Y
                            },
                            LocalTimestamp =
                                DateTimeUtils.ConvertUtcToLocalTime(CoordinateTimeZoneConverter.ConvertUtcToLocal(
                                    l.Coordinates.Y, l.Coordinates.X,
                                    DateTime.SpecifyKind(l.LocalTimestamp, DateTimeKind.Utc)), l.TimeZoneId),
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
                            l.Altitude,
                            l.Accuracy,
                            l.Speed
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

        /// <summary>
        /// Get chronological location data for the authenticated user.
        /// Supports day, month, and year filtering.
        /// This endpoint is available for mobile app access using API tokens.
        /// </summary>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Year to filter</param>
        /// <param name="month">Month to filter (1-12)</param>
        /// <param name="day">Day to filter (1-31)</param>
        [HttpGet("chronological")]
        public async Task<IActionResult> GetChronological(string dateType, int year, int? month = null, int? day = null)
        {
            try
            {
                ApplicationUser? user = GetUserFromToken();
                if (user == null)
                {
                    return Unauthorized(new { success = false, message = "Invalid or missing API token." });
                }

                if (!user.IsActive)
                {
                    return Forbid("User is not active.");
                }

                var (locations, totalItems) = await _chronologicalLocationService.GetLocationsByDateAsync(
                    user.Id, dateType, year, month, day, CancellationToken.None);

                return Ok(new
                {
                    success = true,
                    data = locations,
                    totalItems = totalItems,
                    dateType = dateType,
                    year = year,
                    month = month,
                    day = day
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Invalid arguments for chronological data request");
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chronological data");
                return StatusCode(500, new { success = false, message = "An error occurred while fetching data." });
            }
        }

        /// <summary>
        /// Check if user has data for a specific date.
        /// Used for conditional prev/next date navigation in mobile app.
        /// </summary>
        /// <param name="date">Date to check (ISO format)</param>
        [HttpGet("has-data-for-date")]
        public async Task<IActionResult> HasDataForDate(string date)
        {
            try
            {
                ApplicationUser? user = GetUserFromToken();
                if (user == null)
                {
                    return Unauthorized(new { hasData = false, message = "Invalid or missing API token." });
                }

                if (!user.IsActive)
                {
                    return Forbid("User is not active.");
                }

                if (!DateTime.TryParse(date, out DateTime parsedDate))
                {
                    return BadRequest(new { hasData = false, message = "Invalid date format." });
                }

                bool hasData = await _chronologicalLocationService.HasDataForDateAsync(
                    user.Id, parsedDate, CancellationToken.None);

                return Ok(new { hasData = hasData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking data availability for date");
                return StatusCode(500, new { hasData = false });
            }
        }
    }

    /// <summary>
    /// Result class for check-in rate limit validation.
    /// </summary>
    public class CheckInRateLimitResult
    {
        /// <summary>
        /// Whether the check-in request is allowed based on rate limits.
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Reason message if the request is not allowed.
        /// </summary>
        public string? Reason { get; set; }
    }
}