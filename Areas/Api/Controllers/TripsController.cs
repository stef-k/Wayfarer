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
    /// Same implementation as before, but only used for boundary calculation
    /// </summary>
    private BoundingBox? CalculateTripBoundingBox(Trip trip)
    {
        double? minLat = null, maxLat = null, minLng = null, maxLng = null;

        // Process places (points)
        foreach (var region in trip.Regions ?? new List<Region>())
        {
            foreach (var place in region.Places ?? new List<Place>())
            {
                if (place.Location != null)
                {
                    var lat = place.Location.Y;
                    var lng = place.Location.X;

                    minLat = minLat == null ? lat : Math.Min(minLat.Value, lat);
                    maxLat = maxLat == null ? lat : Math.Max(maxLat.Value, lat);
                    minLng = minLng == null ? lng : Math.Min(minLng.Value, lng);
                    maxLng = maxLng == null ? lng : Math.Max(maxLng.Value, lng);
                }
            }

            // Process areas (polygons)
            foreach (var area in region.Areas ?? new List<Area>())
            {
                if (area.Geometry != null)
                {
                    var envelope = area.Geometry.EnvelopeInternal;

                    minLat = minLat == null ? envelope.MinY : Math.Min(minLat.Value, envelope.MinY);
                    maxLat = maxLat == null ? envelope.MaxY : Math.Max(maxLat.Value, envelope.MaxY);
                    minLng = minLng == null ? envelope.MinX : Math.Min(minLng.Value, envelope.MinX);
                    maxLng = maxLng == null ? envelope.MaxX : Math.Max(maxLng.Value, envelope.MaxX);
                }
            }
        }

        // Process segments (routes)
        foreach (var segment in trip.Segments ?? new List<Segment>())
        {
            if (segment.RouteGeometry != null)
            {
                var envelope = segment.RouteGeometry.EnvelopeInternal;

                minLat = minLat == null ? envelope.MinY : Math.Min(minLat.Value, envelope.MinY);
                maxLat = maxLat == null ? envelope.MaxY : Math.Max(maxLat.Value, envelope.MaxY);
                minLng = minLng == null ? envelope.MinX : Math.Min(minLng.Value, envelope.MinX);
                maxLng = maxLng == null ? envelope.MaxX : Math.Max(maxLng.Value, envelope.MaxX);
            }
        }

        // Return null if no geographic data found
        if (minLat == null || maxLat == null || minLng == null || maxLng == null)
        {
            return null;
        }

        // Add small buffer around bounding box (0.01 degrees ≈ 1km)
        const double buffer = 0.01;
        return new BoundingBox
        {
            North = maxLat.Value + buffer,
            South = minLat.Value - buffer,
            East = maxLng.Value + buffer,
            West = minLng.Value - buffer
        };
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