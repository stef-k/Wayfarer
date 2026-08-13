using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Wayfarer.Models.Dtos;

/// <summary>Classifies bounded public Segment projection failures without exposing aggregate details.</summary>
public enum PublicSegmentFailure
{
    /// <summary>The Segment or one of its anchors belongs outside the authorized Trip.</summary>
    ForeignState,

    /// <summary>A required navigation or referenced entity was not authoritatively loaded.</summary>
    UnloadedOrMissingState,

    /// <summary>Waypoint order, identity, geometry, or index state is contradictory.</summary>
    MalformedState
}

/// <summary>Contains either one trusted public Segment DTO or a bounded failure classification.</summary>
public sealed record PublicSegmentResolution(ApiTripSegmentDto? Segment, PublicSegmentFailure? Failure)
{
    /// <summary>Gets whether projection completed without an expected validation failure.</summary>
    public bool Succeeded => Segment is not null && Failure is null;
}

/// <summary>Validates and projects authoritative Segment state into the public API contract.</summary>
public static class PublicSegmentResolver
{
    private const decimal AnchorTolerance = 0.0000001m;
    private static readonly GeoJsonWriter GeoJsonWriter = new();

    /// <summary>Resolves one tracked, explicitly loaded Segment for an already-authorized Trip.</summary>
    public static PublicSegmentResolution Resolve(
        Segment segment,
        Guid authorizedTripId,
        ApplicationDbContext dbContext)
    {
        if (segment.TripId != authorizedTripId)
            return Failure(PublicSegmentFailure.ForeignState);

        if (!ReferencesAreLoaded(segment, dbContext))
            return Failure(PublicSegmentFailure.UnloadedOrMissingState);

        var endpointFailure = ValidateEndpoint(segment.FromPlaceId, segment.FromPlace, authorizedTripId, dbContext)
            ?? ValidateEndpoint(segment.ToPlaceId, segment.ToPlace, authorizedTripId, dbContext);
        if (endpointFailure.HasValue) return Failure(endpointFailure.Value);

        var orderedWaypoints = segment.Waypoints.OrderBy(item => item.Position).ToArray();
        var waypointFailure = ValidateWaypoints(segment, orderedWaypoints, authorizedTripId, dbContext);
        if (waypointFailure.HasValue) return Failure(waypointFailure.Value);

        var routeResolution = ResolveRoute(segment, orderedWaypoints);
        if (routeResolution.Failure.HasValue) return Failure(routeResolution.Failure.Value);

        return new PublicSegmentResolution(new ApiTripSegmentDto
        {
            Id = segment.Id,
            Mode = segment.Mode ?? string.Empty,
            EstimatedDistanceKm = segment.EstimatedDistanceKm,
            EstimatedDurationMinutes = segment.EstimatedDuration?.TotalMinutes,
            Notes = segment.Notes,
            DisplayOrder = segment.DisplayOrder,
            FromPlaceId = segment.FromPlaceId,
            ToPlaceId = segment.ToPlaceId,
            RouteJson = routeResolution.RouteJson,
            Waypoints = orderedWaypoints.Select(item => new ApiTripSegmentWaypointDto
            {
                PlaceId = item.PlaceId,
                Position = item.Position,
                RouteVertexIndex = item.RouteVertexIndex
            }).ToArray(),
            HasCustomRoute = segment.RouteGeometry is not null
        }, null);
    }

    private static bool ReferencesAreLoaded(Segment segment, ApplicationDbContext dbContext) =>
        dbContext.Entry(segment).Collection(item => item.Waypoints).IsLoaded
        && dbContext.Entry(segment).Reference(item => item.FromPlace).IsLoaded
        && dbContext.Entry(segment).Reference(item => item.ToPlace).IsLoaded;

    private static PublicSegmentFailure? ValidateEndpoint(
        Guid? expectedId,
        Place? place,
        Guid authorizedTripId,
        ApplicationDbContext dbContext)
    {
        if (!expectedId.HasValue) return place is null ? null : PublicSegmentFailure.MalformedState;
        if (place is null || place.Id != expectedId.Value)
            return PublicSegmentFailure.UnloadedOrMissingState;
        if (!dbContext.Entry(place).Reference(item => item.Region).IsLoaded)
            return PublicSegmentFailure.UnloadedOrMissingState;
        return place.Region is null || place.Region.TripId != authorizedTripId
            ? PublicSegmentFailure.ForeignState
            : null;
    }

    private static PublicSegmentFailure? ValidateWaypoints(
        Segment segment,
        IReadOnlyList<SegmentWaypoint> waypoints,
        Guid authorizedTripId,
        ApplicationDbContext dbContext)
    {
        var identities = new HashSet<Guid>();
        for (var position = 0; position < waypoints.Count; position++)
        {
            var waypoint = waypoints[position];
            if (waypoint.SegmentId != segment.Id || waypoint.Position != position
                || !identities.Add(waypoint.PlaceId)
                || waypoint.PlaceId == segment.FromPlaceId || waypoint.PlaceId == segment.ToPlaceId)
                return PublicSegmentFailure.MalformedState;
            if (waypoint.Place is null || waypoint.Place.Id != waypoint.PlaceId)
                return PublicSegmentFailure.UnloadedOrMissingState;
            if (!dbContext.Entry(waypoint.Place).Reference(item => item.Region).IsLoaded)
                return PublicSegmentFailure.UnloadedOrMissingState;
            if (waypoint.Place.Region is null || waypoint.Place.Region.TripId != authorizedTripId)
                return PublicSegmentFailure.ForeignState;
        }

        if (waypoints.Count > 0 && (!segment.FromPlaceId.HasValue || !segment.ToPlaceId.HasValue
            || !HasValidLocation(segment.FromPlace!) || !HasValidLocation(segment.ToPlace!)
            || waypoints.Any(item => !HasValidLocation(item.Place))))
            return PublicSegmentFailure.MalformedState;

        return null;
    }

    private static (string? RouteJson, PublicSegmentFailure? Failure) ResolveRoute(
        Segment segment,
        IReadOnlyList<SegmentWaypoint> waypoints)
    {
        if (segment.RouteGeometry is null)
        {
            if (waypoints.Count == 0) return (null, null);
            var coordinates = new[] { segment.FromPlace!.Location!.Coordinate.Copy() }
                .Concat(waypoints.Select(item => item.Place.Location!.Coordinate.Copy()))
                .Append(segment.ToPlace!.Location!.Coordinate.Copy())
                .ToArray();
            return (Write(new LineString(coordinates) { SRID = 4326 }), null);
        }

        var route = segment.RouteGeometry;
        if (!IsValidLineString(route)) return (null, PublicSegmentFailure.MalformedState);
        if (segment.FromPlace is not null && (!HasValidLocation(segment.FromPlace)
            || !Matches(route.GetCoordinateN(0), segment.FromPlace.Location!.Coordinate)))
            return (null, PublicSegmentFailure.MalformedState);
        if (segment.ToPlace is not null && (!HasValidLocation(segment.ToPlace)
            || !Matches(route.GetCoordinateN(route.NumPoints - 1), segment.ToPlace.Location!.Coordinate)))
            return (null, PublicSegmentFailure.MalformedState);

        var priorIndex = 0;
        var usedIndices = new HashSet<int>();
        foreach (var waypoint in waypoints)
        {
            if (!waypoint.RouteVertexIndex.HasValue) return (null, PublicSegmentFailure.MalformedState);
            var index = waypoint.RouteVertexIndex.Value;
            if (!usedIndices.Add(index) || index <= priorIndex || index <= 0 || index >= route.NumPoints - 1
                || !Matches(route.GetCoordinateN(index), waypoint.Place.Location!.Coordinate))
                return (null, PublicSegmentFailure.MalformedState);
            priorIndex = index;
        }

        return (Write((LineString)route.Copy()), null);
    }

    private static bool IsValidLineString(LineString route) =>
        route.SRID == 4326 && !route.IsEmpty && route.NumPoints >= 2 && route.IsValid
        && route.Coordinates.All(IsValidCoordinate);

    private static bool HasValidLocation(Place place) =>
        place.Location is { IsEmpty: false, SRID: 4326 } location && IsValidCoordinate(location.Coordinate);

    private static bool IsValidCoordinate(Coordinate coordinate) =>
        double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y)
        && coordinate.X is >= -180d and <= 180d && coordinate.Y is >= -90d and <= 90d;

    private static bool Matches(Coordinate actual, Coordinate expected) =>
        IsValidCoordinate(actual) && IsValidCoordinate(expected)
        && Math.Abs((decimal)actual.X - (decimal)expected.X) <= AnchorTolerance
        && Math.Abs((decimal)actual.Y - (decimal)expected.Y) <= AnchorTolerance;

    private static string Write(LineString route) => GeoJsonWriter.Write(route);

    private static PublicSegmentResolution Failure(PublicSegmentFailure failure) => new(null, failure);
}
