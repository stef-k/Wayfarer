using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Applies place-specific route geometry and order side effects.
/// </summary>
public sealed class TripEditorPlaceRouteEffects
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes route and order side effects for place mutations.
    /// </summary>
    public TripEditorPlaceRouteEffects(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Rewrites or clears endpoint route geometries affected by a place coordinate change.
    /// </summary>
    public IReadOnlyList<Segment> RewriteEndpointRoutes(Trip trip, Guid placeId, EditorCoordinateDto? location)
    {
        var affected = trip.Segments.Where(s => s.FromPlaceId == placeId || s.ToPlaceId == placeId).ToList();
        foreach (var segment in affected)
        {
            if (segment.RouteGeometry == null)
            {
                continue;
            }

            if (location == null || segment.RouteGeometry.NumPoints < 2)
            {
                segment.RouteGeometry = null;
                continue;
            }

            var coordinates = segment.RouteGeometry.Coordinates.ToArray();
            var endpoint = new Coordinate(location.Longitude, location.Latitude);
            if (segment.FromPlaceId == placeId)
            {
                coordinates[0] = endpoint;
            }

            if (segment.ToPlaceId == placeId)
            {
                coordinates[^1] = endpoint;
            }

            segment.RouteGeometry = new LineString(coordinates) { SRID = 4326 };
        }

        return affected;
    }

    /// <summary>
    /// Reindexes place display order in one region.
    /// </summary>
    public async Task NormalizePlaceOrdersAsync(Guid regionId, CancellationToken cancellationToken)
    {
        var places = await _dbContext.Places
            .Where(p => p.RegionId == regionId)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < places.Count; i++)
        {
            places[i].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reindexes segment display order after endpoint segment deletion.
    /// </summary>
    public async Task NormalizeSegmentOrdersAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
