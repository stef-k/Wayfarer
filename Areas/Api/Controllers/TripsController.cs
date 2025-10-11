using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;


namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/[controller]")]
[ApiController]
public class TripsController : BaseApiController
{
    public TripsController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger)
        : base(dbContext, logger)
    {
    }

    private const string ShadowRegionName = "Unassigned Places";

    /// <summary>
    /// Returns a list of trips owned by the authenticated user.
    /// </summary>
    /// <remarks>
    /// Requires a valid API token in the Authorization header.
    /// Example: <c>Authorization: Bearer {your_token}</c>
    /// </remarks>
    /// <returns>
    /// A JSON array of trip summaries:
    /// <code>
    /// [
    ///   {
    ///     "id": "b1b40e6e-5e7d-41fc-a2a4-0a773dc1cf7b",
    ///     "name": "Summer 2024",
    ///     "updatedAt": "2025-07-15T14:35:00Z",
    ///     "isPublic": true
    ///   },
    ///   ...
    /// ]
    /// </code>
    /// </returns>
    /// <response code="200">Returns the list of user's trips</response>
    /// <response code="401">If the API token is missing or invalid</response>
    [HttpGet]
    public IActionResult GetUserTrips()
    {
        var user = GetUserFromToken();
        if (user == null)
            return Unauthorized("Missing or invalid API token.");

        var trips = _dbContext.Trips
            .Where(t => t.UserId == user.Id)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.UpdatedAt,
                t.IsPublic
            })
            .ToList();

        return Ok(trips);
    }

    /// <summary>
    /// Returns the full trip structure for the given trip ID.
    /// </summary>
    /// <param name="id">The unique identifier of the trip.</param>
    /// <remarks>
    /// - If the trip is public, no authentication is needed.  
    /// - If the trip is private, a valid API token must be included in the Authorization header.  
    /// Example: <c>Authorization: Bearer {your_token}</c>  
    ///
    /// The response includes trip metadata, regions, places, areas, and segments, with all related notes and geometries.
    /// </remarks>
    /// <returns>
    /// A JSON object representing the complete trip structure.
    /// </returns>
    /// <response code="200">Returns the full trip data</response>
    /// <response code="401">If the trip is private and the token is missing or invalid</response>
    /// <response code="404">If the trip does not exist</response>
    [HttpGet("{id}")]
    public IActionResult GetTrip(Guid id)
    {
        var trip = _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .AsNoTracking()
            .FirstOrDefault(t => t.Id == id);

        if (trip == null)
        {
            return NotFound();
        }

        if (!trip.IsPublic)
        {
            var user = GetUserFromToken();
            if (user == null || user.Id != trip.UserId)
            {
                return Unauthorized("Trip is private or token invalid.");
            }
        }

        foreach (var place in trip.Regions.SelectMany(r => r.Places))
        {
            if (place.Location != null)
            {
                place.Location = SanitizePoint(place.Location);
            }
        }

        foreach (var area in trip.Regions.SelectMany(r => r.Areas))
        {
            if (area.Geometry != null && area.Geometry.SRID != 4326)
            {
                area.Geometry.SRID = 4326;
            }
        }

        foreach (var seg in trip.Segments)
        {
            if (seg.RouteGeometry != null && seg.RouteGeometry.SRID != 4326)
            {
                seg.RouteGeometry.SRID = 4326;
            }
        }

        var dto = trip.ToApiDto();

// Ensure area FillHex default when missing (matches web default #3388FF)
if (dto.Regions != null)
{
    foreach (var r in dto.Regions)
    {
        if (r?.Areas == null) continue;
        foreach (var a in r.Areas)
        {
            if (string.IsNullOrWhiteSpace(a.FillHex))
            {
                a.FillHex = "#3388FF";
            }
        }
    }
}

return Ok(dto);
    }

    /// <summary>
    /// Gets trip geographic boundaries for mobile offline caching.
    /// Mobile app uses this to calculate which tiles to download based on zoom levels and usage patterns.
    /// </summary>
    /// <param name="id">Trip ID</param>
    /// <returns>Trip bounding box and metadata</returns>
    /// <remarks>
    /// This endpoint provides only the essential geographic information.
    /// The mobile app handles all tile coordinate calculations, zoom level decisions,
    /// and adaptive downloading based on user behavior and available storage.
    /// 
    /// Authorization rules:
    /// - Public trips: No authentication required
    /// - Private trips: Must be trip owner OR have valid API token for trip owner
    /// 
    /// Example response:
    /// <code>
    /// {
    ///   "tripId": "b1b40e6e-5e7d-41fc-a2a4-0a773dc1cf7b",
    ///   "name": "Japan 2024",
    ///   "boundingBox": {
    ///     "north": 35.8,
    ///     "south": 34.6, 
    ///     "east": 139.8,
    ///     "west": 135.4
    ///   },
    ///   "routingFile": null
    /// }
    /// </code>
    /// </remarks>
    /// <response code="200">Returns trip boundary information</response>
    /// <response code="401">If trip is private and user is not the owner</response>
    /// <response code="404">If trip not found</response>
    [HttpGet("{id}/boundary")]
    public async Task<IActionResult> GetTripBoundary(Guid id)
    {
        var user = GetUserFromToken();
        try
        {
            // Get trip with all related data
            var trip = await _dbContext.Trips
                .Include(t => t.Regions).ThenInclude(r => r.Places)
                .Include(t => t.Regions).ThenInclude(r => r.Areas)
                .Include(t => t.Segments)
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);

            if (trip == null)
            {
                return NotFound($"Trip with ID {id} not found.");
            }

            // Check authorization - user can always access their own trips
            if (!trip.IsPublic)
            {
                if (user == null || user.Id != trip.UserId)
                {
                    return Unauthorized("Trip is private and you are not the owner.");
                }
            }

            // Calculate trip bounding box
            var boundingBox = CalculateTripBoundingBox(trip);
            if (boundingBox == null)
            {
                return BadRequest("Trip has no geographic data.");
            }

            // Create DTO response
            var response = new TripBoundaryDto
            {
                TripId = trip.Id,
                Name = trip.Name,
                BoundingBox = new BoundingBoxDto
                {
                    North = boundingBox.North,
                    South = boundingBox.South,
                    East = boundingBox.East,
                    West = boundingBox.West
                }
            };

            _logger.LogInformation(
                $"Provided boundary for trip {trip.Name} (ID: {id}) to user {user?.Id ?? "anonymous"}");

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting trip boundary for trip {id}");
            return StatusCode(500, "Internal server error while getting trip boundary.");
        }
    }


    /// <summary>
    /// Calculates the bounding box that encompasses all geographic data in a trip
    /// IMPROVED VERSION with smart buffer calculation based on trip extent and type
    /// </summary>
    private BoundingBox? CalculateTripBoundingBox(Trip trip)
    {
        double? minLat = null, maxLat = null, minLng = null, maxLng = null;
        int totalPoints = 0;

        // Process places (points)
        foreach (var region in trip.Regions ?? new List<Region>())
        {
            foreach (var place in region.Places ?? new List<Place>())
            {
                if (place.Location != null && IsValidCoordinate(place.Location.Y, place.Location.X))
                {
                    var lat = place.Location.Y;
                    var lng = place.Location.X;

                    minLat = minLat == null ? lat : Math.Min(minLat.Value, lat);
                    maxLat = maxLat == null ? lat : Math.Max(maxLat.Value, lat);
                    minLng = minLng == null ? lng : Math.Min(minLng.Value, lng);
                    maxLng = maxLng == null ? lng : Math.Max(maxLng.Value, lng);
                    totalPoints++;
                }
            }

            // Process areas (polygons)
            foreach (var area in region.Areas ?? new List<Area>())
            {
                if (area.Geometry != null)
                {
                    try
                    {
                        var envelope = area.Geometry.EnvelopeInternal;

                        if (IsValidCoordinate(envelope.MinY, envelope.MinX) &&
                            IsValidCoordinate(envelope.MaxY, envelope.MaxX))
                        {
                            minLat = minLat == null ? envelope.MinY : Math.Min(minLat.Value, envelope.MinY);
                            maxLat = maxLat == null ? envelope.MaxY : Math.Max(maxLat.Value, envelope.MaxY);
                            minLng = minLng == null ? envelope.MinX : Math.Min(minLng.Value, envelope.MinX);
                            maxLng = maxLng == null ? envelope.MaxX : Math.Max(maxLng.Value, envelope.MaxX);
                            totalPoints += 2; // Count as 2 points (min/max)
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process envelope for area {AreaId}", area.Id);
                    }
                }
            }
        }

        // Process segments (routes)
        foreach (var segment in trip.Segments ?? new List<Segment>())
        {
            if (segment.RouteGeometry != null)
            {
                try
                {
                    var envelope = segment.RouteGeometry.EnvelopeInternal;

                    if (IsValidCoordinate(envelope.MinY, envelope.MinX) &&
                        IsValidCoordinate(envelope.MaxY, envelope.MaxX))
                    {
                        minLat = minLat == null ? envelope.MinY : Math.Min(minLat.Value, envelope.MinY);
                        maxLat = maxLat == null ? envelope.MaxY : Math.Max(maxLat.Value, envelope.MaxY);
                        minLng = minLng == null ? envelope.MinX : Math.Min(minLng.Value, envelope.MinX);
                        maxLng = maxLng == null ? envelope.MaxX : Math.Max(maxLng.Value, envelope.MaxX);
                        totalPoints += 2; // Count as 2 points (min/max)
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process envelope for segment {SegmentId}", segment.Id);
                }
            }
        }

        // Return null if no geographic data found
        if (minLat == null || maxLat == null || minLng == null || maxLng == null)
        {
            _logger.LogWarning("No valid geographic data found for trip {TripId}", trip.Id);
            return null;
        }

        // Calculate smart buffer based on trip characteristics
        var buffer = CalculateSmartBuffer(minLat.Value, maxLat.Value, minLng.Value, maxLng.Value, totalPoints,
            trip.Name);

        var boundingBox = new BoundingBox
        {
            North = maxLat.Value + buffer,
            South = minLat.Value - buffer,
            East = maxLng.Value + buffer,
            West = minLng.Value - buffer
        };

        // Log detailed information for debugging
        var latSpan = maxLat.Value - minLat.Value;
        var lngSpan = maxLng.Value - minLng.Value;
        _logger.LogInformation("Trip {TripId} ({TripName}) boundary calculation:", trip.Id, trip.Name);
        _logger.LogInformation("  Raw bounds: Lat({MinLat:F6}, {MaxLat:F6}), Lng({MinLng:F6}, {MaxLng:F6})",
            minLat.Value, maxLat.Value, minLng.Value, maxLng.Value);
        _logger.LogInformation("  Span: {LatSpan:F3}° lat × {LngSpan:F3}° lng", latSpan, lngSpan);
        _logger.LogInformation("  Data points: {TotalPoints}", totalPoints);
        _logger.LogInformation("  Buffer applied: {Buffer:F4}° (~{BufferKm:F1}km)", buffer, buffer * 111);
        _logger.LogInformation("  Final bounds: N:{North:F6}, S:{South:F6}, E:{East:F6}, W:{West:F6}",
            boundingBox.North, boundingBox.South, boundingBox.East, boundingBox.West);

        return boundingBox;
    }

    /// <summary>
    /// Calculates a smart buffer size based on trip characteristics
    /// </summary>
    private static double CalculateSmartBuffer(double minLat, double maxLat, double minLng, double maxLng,
        int totalPoints, string tripName)
    {
        var latSpan = maxLat - minLat;
        var lngSpan = maxLng - minLng;
        var maxSpan = Math.Max(latSpan, lngSpan);

        // Determine trip scale category
        var tripScale = ClassifyTripScale(maxSpan);

        // Base buffer calculation with multiple factors
        double buffer;

        switch (tripScale)
        {
            case TripScale.CityLevel:
                // City trips (< 0.2°): Small buffer, focus on accuracy
                buffer = Math.Max(0.0075, maxSpan * 0.15); // 0.8-3km buffer (+50%)
                break;

            case TripScale.RegionalLevel:
                // Regional trips (0.2-2°): Medium buffer for surrounding areas  
                buffer = Math.Max(0.03, maxSpan * 0.075); // 3-17km buffer (+50%)
                break;

            case TripScale.CountryLevel:
                // Country trips (2-10°): Large buffer to ensure full coverage
                buffer = Math.Max(0.15, maxSpan * 0.045); // 17-50km buffer (+50%)
                break;

            case TripScale.ContinentalLevel:
                // Multi-country trips (> 10°): Very large buffer
                buffer = Math.Max(0.3, maxSpan * 0.03); // 33-66km buffer (+50%)
                break;

            default:
                buffer = 0.075; // Fallback 8.3km (+50%)
                break;
        }

        // Adjust buffer based on data density
        if (totalPoints < 5)
        {
            // Few data points = increase buffer for safety
            buffer *= 1.5;
        }
        else if (totalPoints > 50)
        {
            // Many data points = can reduce buffer slightly
            buffer *= 0.8;
        }

        // Ensure reasonable limits
        buffer = Math.Min(buffer, 0.75); // Max ~83km buffer (+50%)
        buffer = Math.Max(buffer, 0.003); // Min ~300m buffer (+50%)

        return buffer;
    }

    /// <summary>
    /// Classification of trip scales for buffer calculation
    /// </summary>
    private enum TripScale
    {
        CityLevel, // < 0.2° (~22km)
        RegionalLevel, // 0.2° - 2° (~22km - 220km) 
        CountryLevel, // 2° - 10° (~220km - 1100km)
        ContinentalLevel // > 10° (~1100km+)
    }

    /// <summary>
    /// Classifies a trip based on its geographic extent
    /// </summary>
    private static TripScale ClassifyTripScale(double maxSpanDegrees)
    {
        if (maxSpanDegrees < 0.2) return TripScale.CityLevel;
        if (maxSpanDegrees < 2.0) return TripScale.RegionalLevel;
        if (maxSpanDegrees < 10.0) return TripScale.CountryLevel;
        return TripScale.ContinentalLevel;
    }

    /// <summary>
    /// Validates that latitude and longitude coordinates are within valid ranges
    /// </summary>
    private static bool IsValidCoordinate(double latitude, double longitude)
    {
        return latitude >= -90.0 && latitude <= 90.0 &&
               longitude >= -180.0 && longitude <= 180.0 &&
               !double.IsNaN(latitude) && !double.IsNaN(longitude) &&
               !double.IsInfinity(latitude) && !double.IsInfinity(longitude);
    }

    /// <summary>
    /// Creates a new Place within the given trip. If regionId is omitted, the place is created under
    /// the trip's "Unassigned Places" region.
    /// </summary>
    /// <param name="tripId">Trip ID (must belong to the token user)</param>
    /// <param name="request">Place creation payload</param>
    [HttpPost("{tripId}/places")]
    public async Task<IActionResult> CreatePlace(Guid tripId, [FromBody] PlaceCreateRequestDto request)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");

        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == user.Id);
        if (trip == null) return NotFound("Trip not found.");

        if (request == null) return BadRequest("Invalid request.");
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name is required.");

        // Resolve destination region
        Region destRegion;
        if (request.RegionId.HasValue)
        {
            destRegion = await _dbContext.Regions.FirstOrDefaultAsync(r => r.Id == request.RegionId.Value);
            if (destRegion == null || destRegion.TripId != tripId || destRegion.UserId != user.Id)
                return BadRequest("Invalid regionId.");
        }
        else
        {
            destRegion = await GetOrCreateUnassignedRegion(trip, user.Id);
        }

        // Coordinates validation
        NetTopologySuite.Geometries.Point? location = null;
        if (request.Latitude.HasValue || request.Longitude.HasValue)
        {
            if (!(request.Latitude.HasValue && request.Longitude.HasValue))
                return BadRequest("Both latitude and longitude must be provided together.");
            double lat = request.Latitude.Value; double lon = request.Longitude.Value;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return BadRequest("Latitude or Longitude is out of range.");
            location = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
        }

        // Defaults for icon and color
        string iconName = string.IsNullOrWhiteSpace(request.IconName) ? "marker" : request.IconName!;
        string markerColor = string.IsNullOrWhiteSpace(request.MarkerColor) ? "bg-blue" : request.MarkerColor!;

        // Display order
        int displayOrder = request.DisplayOrder ?? await GetNextPlaceOrder(destRegion.Id);

        var place = new Place
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RegionId = destRegion.Id,
            Name = request.Name!,
            Notes = request.Notes,
            Location = location,
            DisplayOrder = displayOrder,
            IconName = iconName,
            MarkerColor = markerColor,
        };

        _dbContext.Places.Add(place);
        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, place });
    }

    /// <summary>
    /// Partially updates an existing Place by ID. Allows moving across regions that belong to trips
    /// owned by the same user.
    /// </summary>
    /// <param name="placeId">Place ID</param>
    /// <param name="request">Partial update payload</param>
    [HttpPut("places/{placeId}")]
    public async Task<IActionResult> UpdatePlace(Guid placeId, [FromBody] PlaceUpdateRequestDto request)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");
        if (request == null) return BadRequest("Invalid request.");

        var place = await _dbContext.Places
            .Include(p => p.Region)
            .ThenInclude(r => r.Trip)
            .FirstOrDefaultAsync(p => p.Id == placeId);
        if (place == null) return NotFound("Place not found.");
        if (place.Region.Trip.UserId != user.Id) return Unauthorized("Not your place.");

        bool anyChange = false;

        // Region move
        if (request.RegionId.HasValue && request.RegionId.Value != place.RegionId)
        {
            var newRegion = await _dbContext.Regions
                .Include(r => r.Trip)
                .FirstOrDefaultAsync(r => r.Id == request.RegionId.Value);
            if (newRegion == null || newRegion.Trip.UserId != user.Id)
                return BadRequest("Invalid regionId.");

            place.RegionId = newRegion.Id;
            // Default order at end if not explicitly given
            if (!request.DisplayOrder.HasValue)
                place.DisplayOrder = await GetNextPlaceOrder(newRegion.Id);
            anyChange = true;
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            place.Name = request.Name!;
            anyChange = true;
        }

        // Coordinates
        if (request.Latitude.HasValue || request.Longitude.HasValue)
        {
            if (!(request.Latitude.HasValue && request.Longitude.HasValue))
                return BadRequest("Both latitude and longitude must be provided together.");
            double lat = request.Latitude.Value; double lon = request.Longitude.Value;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return BadRequest("Latitude or Longitude is out of range.");
            place.Location = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
            anyChange = true;
        }

        if (request.Notes != null)
        {
            place.Notes = request.Notes;
            anyChange = true;
        }

        if (request.DisplayOrder.HasValue)
        {
            place.DisplayOrder = request.DisplayOrder.Value;
            anyChange = true;
        }

        // Icon resets/updates
        if (request.ClearIcon == true || (request.IconName != null && string.IsNullOrWhiteSpace(request.IconName)))
        {
            place.IconName = "marker";
            anyChange = true;
        }
        else if (request.IconName != null)
        {
            place.IconName = request.IconName;
            anyChange = true;
        }

        if (request.ClearMarkerColor == true || (request.MarkerColor != null && string.IsNullOrWhiteSpace(request.MarkerColor)))
        {
            place.MarkerColor = "bg-blue";
            anyChange = true;
        }
        else if (request.MarkerColor != null)
        {
            place.MarkerColor = request.MarkerColor;
            anyChange = true;
        }

        if (!anyChange) return Ok(new { success = true, message = "No changes applied.", place });

        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, place });
    }

    /// <summary>
    /// Deletes a place by ID.
    /// </summary>
    [HttpDelete("places/{placeId}")]
    public async Task<IActionResult> DeletePlace(Guid placeId)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");

        var place = await _dbContext.Places
            .Include(p => p.Region).ThenInclude(r => r.Trip)
            .FirstOrDefaultAsync(p => p.Id == placeId);
        if (place == null) return NotFound("Place not found.");
        if (place.Region.Trip.UserId != user.Id) return Unauthorized("Not your place.");

        _dbContext.Places.Remove(place);
        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, message = "Place deleted.", placeId });
    }

    /// <summary>
    /// Creates a new region inside the trip.
    /// </summary>
    [HttpPost("{tripId}/regions")]
    public async Task<IActionResult> CreateRegion(Guid tripId, [FromBody] RegionCreateRequestDto request)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");
        if (request == null) return BadRequest("Invalid request.");
        if (string.Equals(request.Name?.Trim(), ShadowRegionName, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Region name is reserved.");

        var trip = await _dbContext.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == user.Id);
        if (trip == null) return NotFound("Trip not found.");

        // Center point
        NetTopologySuite.Geometries.Point? center = null;
        if (request.CenterLatitude.HasValue || request.CenterLongitude.HasValue)
        {
            if (!(request.CenterLatitude.HasValue && request.CenterLongitude.HasValue))
                return BadRequest("Both centerLatitude and centerLongitude must be provided together.");
            double lat = request.CenterLatitude.Value; double lon = request.CenterLongitude.Value;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return BadRequest("Center latitude or longitude is out of range.");
            center = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
        }

        // Display order (exclude Unassigned at 0)
        int displayOrder = request.DisplayOrder ?? await GetNextRegionOrder(tripId);

        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            UserId = user.Id,
            Name = request.Name!.Trim(),
            Notes = request.Notes,
            CoverImageUrl = request.CoverImageUrl,
            Center = center,
            DisplayOrder = displayOrder
        };

        _dbContext.Regions.Add(region);
        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, region });
    }

    /// <summary>
    /// Updates an existing region by ID. Trip association cannot change.
    /// </summary>
    [HttpPut("regions/{regionId}")]
    public async Task<IActionResult> UpdateRegion(Guid regionId, [FromBody] RegionUpdateRequestDto request)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");
        if (request == null) return BadRequest("Invalid request.");

        var region = await _dbContext.Regions.Include(r => r.Trip).FirstOrDefaultAsync(r => r.Id == regionId);
        if (region == null) return NotFound("Region not found.");
        if (region.Trip.UserId != user.Id) return Unauthorized("Not your region.");

        bool anyChange = false;

        if (request.Name != null)
        {
            if (string.Equals(request.Name.Trim(), ShadowRegionName, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Region name is reserved.");
            region.Name = request.Name.Trim();
            anyChange = true;
        }

        if (request.Notes != null) { region.Notes = request.Notes; anyChange = true; }
        if (request.CoverImageUrl != null) { region.CoverImageUrl = request.CoverImageUrl; anyChange = true; }

        if (request.CenterLatitude.HasValue || request.CenterLongitude.HasValue)
        {
            if (!(request.CenterLatitude.HasValue && request.CenterLongitude.HasValue))
                return BadRequest("Both centerLatitude and centerLongitude must be provided together.");
            double lat = request.CenterLatitude.Value; double lon = request.CenterLongitude.Value;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return BadRequest("Center latitude or longitude is out of range.");
            region.Center = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
            anyChange = true;
        }

        if (request.DisplayOrder.HasValue)
        {
            region.DisplayOrder = request.DisplayOrder.Value;
            anyChange = true;
        }

        if (!anyChange) return Ok(new { success = true, message = "No changes applied.", region });

        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, region });
    }

    /// <summary>
    /// Deletes a region by ID and all its children (places, areas). The reserved
    /// "Unassigned Places" region cannot be deleted.
    /// </summary>
    [HttpDelete("regions/{regionId}")]
    public async Task<IActionResult> DeleteRegion(Guid regionId)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");

        var region = await _dbContext.Regions.FirstOrDefaultAsync(r => r.Id == regionId);
        if (region == null) return NotFound("Region not found.");

        // Verify ownership
        var trip = await _dbContext.Trips.FirstOrDefaultAsync(t => t.Id == region.TripId);
        if (trip == null || trip.UserId != user.Id) return Unauthorized("Not your region.");

        if (string.Equals(region.Name, ShadowRegionName, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Cannot delete the Unassigned Places region.");

        _dbContext.Regions.Remove(region);
        await _dbContext.SaveChangesAsync();
        return Ok(new { success = true, message = "Region deleted.", regionId });
    }

    private async Task<Region> GetOrCreateUnassignedRegion(Trip trip, string userId)
    {
        var region = await _dbContext.Regions.FirstOrDefaultAsync(r => r.TripId == trip.Id && r.Name == ShadowRegionName);
        if (region != null) return region;
        region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            Name = ShadowRegionName,
            DisplayOrder = 0
        };
        _dbContext.Regions.Add(region);
        await _dbContext.SaveChangesAsync();
        return region;
    }

    private async Task<int> GetNextPlaceOrder(Guid regionId)
    {
        var max = await _dbContext.Places.Where(p => p.RegionId == regionId).MaxAsync(p => (int?)p.DisplayOrder) ?? 0;
        return max + 1;
    }

    private async Task<int> GetNextRegionOrder(Guid tripId)
    {
        var max = await _dbContext.Regions
            .Where(r => r.TripId == tripId && r.Name != ShadowRegionName)
            .MaxAsync(r => (int?)r.DisplayOrder) ?? 0;
        return Math.Max(max + 1, 1);
    }
}

/// <summary>
/// Internal helper class for bounding box calculations
/// Only used within the controller - not exposed in API
/// </summary>
internal class BoundingBox
{
    public double North { get; set; }
    public double South { get; set; }
    public double East { get; set; }
    public double West { get; set; }
}
