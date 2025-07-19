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
        : base(dbContext, logger) { }

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
            .Select(t => new {
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
}
