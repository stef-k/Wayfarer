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
                },
                RoutingFile = null // Phase 6 feature
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
