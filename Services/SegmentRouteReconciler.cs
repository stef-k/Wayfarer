using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Describes one proposed intermediate saved-place anchor.</summary>
/// <param name="Place">The canonical saved place.</param>
/// <param name="Position">Its zero-based position in the submitted sequence.</param>
/// <param name="RouteVertexIndex">Its custom-route vertex index, or null for fallback geometry.</param>
public sealed record SegmentWaypointProposal(Place Place, int Position, int? RouteVertexIndex);

/// <summary>Reports whether a route proposal was applied and its effective saved-place anchor chain.</summary>
/// <param name="Succeeded">Whether validation succeeded and the tracked aggregate was updated.</param>
/// <param name="Errors">Deterministic aggregate validation errors.</param>
/// <param name="EffectiveAnchorChain">The ordered saved-place anchors used by fallback rendering.</param>
public sealed record SegmentRouteReconciliationResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<Place> EffectiveAnchorChain);

/// <summary>Validates and atomically applies endpoint, waypoint, and custom-route aggregate state.</summary>
public static class SegmentRouteReconciler
{
    /// <summary>Maximum independent longitude or latitude difference accepted for an anchor vertex.</summary>
    public const double CoordinateToleranceDegrees = 0.0000001d;

    /// <summary>Loads the complete tracked aggregate required for authoritative route reconciliation.</summary>
    public static Task<Segment?> LoadAggregateAsync(
        ApplicationDbContext dbContext,
        Guid segmentId,
        CancellationToken cancellationToken = default) =>
        dbContext.Segments
            .Include(segment => segment.FromPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.ToPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.Waypoints.OrderBy(waypoint => waypoint.Position))
                .ThenInclude(waypoint => waypoint.Place).ThenInclude(place => place.Region)
            .SingleOrDefaultAsync(segment => segment.Id == segmentId, cancellationToken);

    /// <summary>
    /// Validates a complete proposal before changing the tracked segment, so rejection cannot partially
    /// replace waypoint rows, endpoints, or geometry.
    /// </summary>
    public static SegmentRouteReconciliationResult Reconcile(
        Segment segment,
        Place? from,
        Place? to,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        LineString? routeGeometry)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(waypoints);

        var errors = Validate(segment.TripId, from, to, waypoints, routeGeometry);
        if (errors.Count > 0)
        {
            return new(false, errors, BuildAnchorChain(from, waypoints, to));
        }

        segment.FromPlaceId = from?.Id;
        segment.FromPlace = from;
        segment.ToPlaceId = to?.Id;
        segment.ToPlace = to;
        segment.RouteGeometry = routeGeometry;
        var existingByPlaceId = segment.Waypoints.ToDictionary(waypoint => waypoint.PlaceId);
        var reconciledWaypoints = new List<SegmentWaypoint>(waypoints.Count);
        foreach (var proposed in waypoints)
        {
            if (!existingByPlaceId.TryGetValue(proposed.Place.Id, out var waypoint))
            {
                waypoint = new SegmentWaypoint
                {
                    SegmentId = segment.Id,
                    Segment = segment,
                    PlaceId = proposed.Place.Id
                };
            }

            waypoint.Place = proposed.Place;
            waypoint.Position = proposed.Position;
            waypoint.RouteVertexIndex = proposed.RouteVertexIndex;
            reconciledWaypoints.Add(waypoint);
        }

        segment.Waypoints = reconciledWaypoints;

        return new(true, [], BuildAnchorChain(from, waypoints, to));
    }

    private static List<string> Validate(
        Guid tripId,
        Place? from,
        Place? to,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        LineString? geometry)
    {
        var errors = new List<string>();
        if (waypoints.Count == 0)
        {
            // Legacy zero-waypoint segments retain their existing optional endpoint contract.
            return errors;
        }

        if (from == null) errors.Add("From place is required when a segment has waypoints.");
        if (to == null) errors.Add("To place is required when a segment has waypoints.");
        if (from != null && !HasValidLocation(from)) errors.Add("From place must have a valid SRID 4326 location when a segment has waypoints.");
        if (to != null && !HasValidLocation(to)) errors.Add("To place must have a valid SRID 4326 location when a segment has waypoints.");
        if (from != null && !BelongsToTrip(from, tripId)) errors.Add("From place must belong to the segment trip.");
        if (to != null && !BelongsToTrip(to, tripId)) errors.Add("To place must belong to the segment trip.");

        var placeIds = new HashSet<Guid>();
        var positions = new HashSet<int>();
        for (var index = 0; index < waypoints.Count; index++)
        {
            var waypoint = waypoints[index];
            if (waypoint.Position != index || !positions.Add(waypoint.Position))
                errors.Add("Waypoint positions must be unique and contiguous from zero in submitted order.");
            if (!placeIds.Add(waypoint.Place.Id))
                errors.Add("Intermediate waypoint places must be unique within a segment.");
            if (waypoint.Place.Id == from?.Id) errors.Add("A waypoint cannot equal the From place.");
            if (waypoint.Place.Id == to?.Id) errors.Add("A waypoint cannot equal the To place.");
            if (!BelongsToTrip(waypoint.Place, tripId)) errors.Add("Every waypoint place must belong to the segment trip.");
            if (!HasValidLocation(waypoint.Place)) errors.Add("Every waypoint place must have a valid SRID 4326 location.");
        }

        if (geometry == null)
        {
            if (waypoints.Any(waypoint => waypoint.RouteVertexIndex.HasValue))
                errors.Add("Fallback geometry requires null waypoint route vertex indices.");
            return errors;
        }

        ValidateCustomGeometry(from, to, waypoints, geometry, errors);
        return errors;
    }

    private static void ValidateCustomGeometry(
        Place? from,
        Place? to,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        LineString geometry,
        List<string> errors)
    {
        if (geometry.SRID != 4326 || geometry.IsEmpty || geometry.NumPoints < 2 || !geometry.IsValid)
        {
            errors.Add("Custom route geometry must be a valid SRID 4326 LineString with at least two vertices.");
            return;
        }

        if (from?.Location != null && !CoordinatesMatch(geometry.GetCoordinateN(0), from.Location.Coordinate))
            errors.Add("The first custom-route vertex must match the From place.");
        if (to?.Location != null && !CoordinatesMatch(geometry.GetCoordinateN(geometry.NumPoints - 1), to.Location.Coordinate))
            errors.Add("The last custom-route vertex must match the To place.");

        var priorIndex = 0;
        var usedIndices = new HashSet<int>();
        foreach (var waypoint in waypoints)
        {
            if (!waypoint.RouteVertexIndex.HasValue)
            {
                errors.Add("Every waypoint requires a route vertex index for custom geometry.");
                continue;
            }

            var vertexIndex = waypoint.RouteVertexIndex.Value;
            if (!usedIndices.Add(vertexIndex)) errors.Add("Waypoint route vertex indices must be unique.");
            if (vertexIndex <= priorIndex) errors.Add("Waypoint route vertex indices must increase in waypoint order.");
            if (vertexIndex <= 0 || vertexIndex >= geometry.NumPoints - 1)
            {
                errors.Add("Waypoint route vertex indices must identify interior route vertices.");
            }
            else if (waypoint.Place.Location != null
                     && !CoordinatesMatch(geometry.GetCoordinateN(vertexIndex), waypoint.Place.Location.Coordinate))
            {
                errors.Add("Each indexed custom-route vertex must match its waypoint place.");
            }

            priorIndex = vertexIndex;
        }
    }

    private static IReadOnlyList<Place> BuildAnchorChain(
        Place? from,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        Place? to)
    {
        var anchors = new List<Place>(waypoints.Count + 2);
        if (from != null) anchors.Add(from);
        anchors.AddRange(waypoints.Select(waypoint => waypoint.Place));
        if (to != null) anchors.Add(to);
        return anchors;
    }

    private static bool BelongsToTrip(Place place, Guid tripId) => place.Region != null && place.Region.TripId == tripId;

    private static bool HasValidLocation(Place place) =>
        place.Location is { IsEmpty: false, SRID: 4326 } location
        && double.IsFinite(location.X)
        && double.IsFinite(location.Y);

    private static bool CoordinatesMatch(Coordinate actual, Coordinate expected)
    {
        if (!double.IsFinite(actual.X) || !double.IsFinite(actual.Y)
            || !double.IsFinite(expected.X) || !double.IsFinite(expected.Y))
        {
            return false;
        }

        // Decimal comparison keeps the contract's seven-decimal boundary inclusive despite binary rounding.
        const decimal tolerance = 0.0000001m;
        return Math.Abs((decimal)actual.X - (decimal)expected.X) <= tolerance
            && Math.Abs((decimal)actual.Y - (decimal)expected.Y) <= tolerance;
    }
}
