using NetTopologySuite.Geometries;

namespace Wayfarer.Models.ViewModels;

/// <summary>Describes one authorized saved-Place position in a viewer Segment journey.</summary>
public sealed record ViewerJourneyAnchor(string Role, Guid? PlaceId, string DisplayName, Point? Location);

/// <summary>Contains presentation-only Segment anchors, safe route geometry, and neutral degradation.</summary>
public sealed record ViewerSegmentJourney(
    Guid SegmentId,
    IReadOnlyList<ViewerJourneyAnchor> Anchors,
    string? RouteWkt,
    int WaypointCount,
    int RoutePointCount,
    string? DegradationMessage)
{
    /// <summary>Gets the map-independent arrow-separated journey when anchor order is trustworthy.</summary>
    public string? TrailText => Anchors.Count >= 2
        ? string.Join(" → ", Anchors.Select(anchor => anchor.DisplayName))
        : null;
}

/// <summary>Projects persisted Segment state into one deterministic, non-mutating viewer journey.</summary>
public static class ViewerSegmentJourneyResolver
{
    private const double AnchorTolerance = 0.0000001;

    /// <summary>Resolves authorized Segment state without repairing malformed or unloaded data.</summary>
    public static ViewerSegmentJourney Resolve(Segment segment, bool waypointsLoaded)
    {
        if (!waypointsLoaded)
        {
            return Degraded(segment, 0, "Journey details are unavailable.");
        }

        var orderedWaypoints = segment.Waypoints.OrderBy(waypoint => waypoint.Position).ToArray();
        if (orderedWaypoints.Where((waypoint, position) => waypoint.Position != position).Any())
        {
            return Degraded(segment, orderedWaypoints.Length, "Journey order is unavailable.");
        }

        var anchors = new List<ViewerJourneyAnchor>(orderedWaypoints.Length + 2)
        {
            Anchor("Start", segment.FromPlaceId, segment.FromPlace)
        };
        anchors.AddRange(orderedWaypoints.Select((waypoint, position) =>
            Anchor($"Via {position + 1}", waypoint.PlaceId, waypoint.Place)));
        anchors.Add(Anchor("End", segment.ToPlaceId, segment.ToPlace));

        var identities = anchors.Select(anchor => anchor.PlaceId).ToArray();
        var allowedClosedLoop = identities.Length >= 2 && identities[0].HasValue && identities[0] == identities[^1];
        var duplicateIdentity = identities.Where(identity => identity.HasValue)
            .GroupBy(identity => identity)
            .Any(group => group.Count() > 1
                && !(allowedClosedLoop && group.Key == identities[0] && group.Count() == 2));
        if (duplicateIdentity)
        {
            return Degraded(segment, orderedWaypoints.Length, "Journey details are unavailable.");
        }

        var customRoute = ResolveCustomRoute(segment, orderedWaypoints, anchors);
        if (segment.RouteGeometry != null)
        {
            return customRoute == null
                ? new(segment.Id, anchors, null, orderedWaypoints.Length, 0, "Route line is unavailable.")
                : new(segment.Id, anchors, customRoute.AsText(), orderedWaypoints.Length, customRoute.NumPoints, null);
        }

        if (anchors.Any(anchor => anchor.Location == null))
        {
            return new(segment.Id, anchors, null, orderedWaypoints.Length, 0, "Route line is unavailable.");
        }

        var fallback = new LineString(anchors.Select(anchor => anchor.Location!.Coordinate.Copy()).ToArray()) { SRID = 4326 };
        return new(segment.Id, anchors, fallback.AsText(), orderedWaypoints.Length, fallback.NumPoints, null);
    }

    /// <summary>Builds one neutral anchor without inventing missing identity, names, or coordinates.</summary>
    private static ViewerJourneyAnchor Anchor(string role, Guid? placeId, Place? place)
    {
        var displayName = place == null
            ? role == "Start" || role == "End" ? "Unavailable place" : "Unavailable intermediate place"
            : string.IsNullOrWhiteSpace(place.Name) ? "Unnamed place" : place.Name.Trim();
        return new(role, place?.Id, displayName, IsValidLocation(place?.Location) ? place!.Location : null);
    }

    /// <summary>Accepts custom geometry only when every semantic anchor mapping remains authoritative.</summary>
    private static LineString? ResolveCustomRoute(
        Segment segment,
        IReadOnlyList<SegmentWaypoint> waypoints,
        IReadOnlyList<ViewerJourneyAnchor> anchors)
    {
        var route = segment.RouteGeometry;
        if (route == null || route.IsEmpty || !route.IsValid || route.NumPoints < 2 || anchors.Any(anchor => anchor.Location == null)
            || route.Coordinates.Any(coordinate => !IsFinite(coordinate)))
        {
            return null;
        }

        if (!Matches(route.Coordinates[0], anchors[0].Location!.Coordinate)
            || !Matches(route.Coordinates[^1], anchors[^1].Location!.Coordinate))
        {
            return null;
        }

        var previousIndex = 0;
        for (var position = 0; position < waypoints.Count; position++)
        {
            var routeIndex = waypoints[position].RouteVertexIndex;
            if (routeIndex is null || routeIndex <= previousIndex || routeIndex >= route.NumPoints - 1
                || !Matches(route.Coordinates[routeIndex.Value], anchors[position + 1].Location!.Coordinate))
            {
                return null;
            }

            previousIndex = routeIndex.Value;
        }

        return (LineString)route.Copy();
    }

    /// <summary>Returns whether a saved Place location is safe for map presentation.</summary>
    private static bool IsValidLocation(Point? point) => point != null
        && IsFinite(point.Coordinate)
        && point.X is >= -180 and <= 180
        && point.Y is >= -90 and <= 90;

    /// <summary>Compares one custom-route anchor using the persisted aggregate tolerance.</summary>
    private static bool Matches(Coordinate left, Coordinate right) =>
        Math.Abs(left.X - right.X) <= AnchorTolerance && Math.Abs(left.Y - right.Y) <= AnchorTolerance;

    /// <summary>Rejects non-finite coordinates rather than passing corrupt geometry to Leaflet.</summary>
    private static bool IsFinite(Coordinate coordinate) =>
        double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y);

    /// <summary>Creates bounded neutral output when semantic ordering cannot be trusted.</summary>
    private static ViewerSegmentJourney Degraded(Segment segment, int waypointCount, string message) =>
        new(segment.Id, [], null, waypointCount, 0, message);
}
