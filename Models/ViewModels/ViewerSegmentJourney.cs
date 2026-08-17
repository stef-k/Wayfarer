using NetTopologySuite.Geometries;

namespace Wayfarer.Models.ViewModels;

/// <summary>Describes one authorized saved-Place position in a viewer Segment journey.</summary>
public sealed record ViewerJourneyAnchor(
    int Position,
    string Label,
    string Role,
    Guid? PlaceId,
    string DisplayName,
    string? RegionName,
    Point? Location,
    int? RouteVertexIndex);

/// <summary>Contains presentation-only Segment anchors, safe route geometry, and neutral degradation.</summary>
public sealed record ViewerSegmentJourney(
    Guid SegmentId,
    IReadOnlyList<ViewerJourneyAnchor> Anchors,
    string? RouteWkt,
    int WaypointCount,
    int RoutePointCount,
    string RouteOrientation,
    string? DegradationMessage)
{
    /// <summary>Gets the map-independent arrow-separated journey when anchor order is trustworthy.</summary>
    public string? TrailText => Anchors.Count >= 2
        ? string.Join(" → ", Anchors.Select(anchor => anchor.DisplayName))
        : null;

    /// <summary>Gets the compact derived-label trail without persisting presentation state.</summary>
    public string? CompactTrail => Anchors.Count >= 2
        ? string.Join(" → ", Anchors.Select(anchor => $"{anchor.Label} {anchor.DisplayName}"))
        : null;

    /// <summary>Gets the keyboard-authoritative journey name in semantic order.</summary>
    public string? AccessibleName => Anchors.Count >= 2
        ? $"Segment from {Anchors[0].DisplayName}{(Anchors.Count > 2 ? $" via {string.Join(", then ", Anchors.Skip(1).SkipLast(1).Select(anchor => anchor.DisplayName))}" : string.Empty)} to {Anchors[^1].DisplayName}"
        : null;
}

/// <summary>Projects persisted Segment state into one deterministic, non-mutating viewer journey.</summary>
public static class ViewerSegmentJourneyResolver
{
    private const double AnchorTolerance = 0.0000001;

    /// <summary>Resolves authorized Segment state without repairing malformed or unloaded data.</summary>
    public static ViewerSegmentJourney Resolve(Segment segment, Guid authorizedTripId, bool waypointsLoaded)
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
            Anchor(0, "Start", segment.FromPlaceId, segment.FromPlace, authorizedTripId, 0)
        };
        anchors.AddRange(orderedWaypoints.Select((waypoint, position) =>
            Anchor(position + 1, $"Via {position + 1}", waypoint.PlaceId, waypoint.Place, authorizedTripId, waypoint.RouteVertexIndex)));
        anchors.Add(Anchor(orderedWaypoints.Length + 1, "End", segment.ToPlaceId, segment.ToPlace, authorizedTripId,
            segment.RouteGeometry == null ? null : segment.RouteGeometry.NumPoints - 1));

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
                ? IsSafeNeutralRoute(segment.RouteGeometry, anchors)
                    ? new(segment.Id, anchors, segment.RouteGeometry.AsText(), orderedWaypoints.Length, segment.RouteGeometry.NumPoints, "ambiguous", "Route direction unavailable")
                    : new(segment.Id, anchors, null, orderedWaypoints.Length, 0, "ambiguous", "Route line is unavailable.")
                : new(segment.Id, anchors, customRoute.Value.Route.AsText(), orderedWaypoints.Length, customRoute.Value.Route.NumPoints, customRoute.Value.Orientation, null);
        }

        if (anchors.Any(anchor => anchor.Location == null))
        {
            return new(segment.Id, anchors, null, orderedWaypoints.Length, 0, "ambiguous", "Route line is unavailable.");
        }

        var fallback = new LineString(anchors.Select(anchor => anchor.Location!.Coordinate.Copy()).ToArray()) { SRID = 4326 };
        return new(segment.Id, anchors, fallback.AsText(), orderedWaypoints.Length, fallback.NumPoints, "forward", null);
    }

    /// <summary>Builds one neutral anchor without inventing missing identity, names, or coordinates.</summary>
    private static ViewerJourneyAnchor Anchor(int position, string role, Guid? placeId, Place? place, Guid authorizedTripId, int? routeVertexIndex)
    {
        var authorizedPlace = place != null && placeId == place.Id && place.Region?.TripId == authorizedTripId
            ? place
            : null;
        var displayName = authorizedPlace == null
            ? role == "Start" ? "Unlinked start" : role == "End" ? "Unlinked end" : "Unnamed waypoint"
            : string.IsNullOrWhiteSpace(authorizedPlace.Name) ? role.StartsWith("Via", StringComparison.Ordinal) ? "Unnamed waypoint" : $"Unlinked {role.ToLowerInvariant()}" : authorizedPlace.Name.Trim();
        return new(position, AlphabeticLabel(position), role, authorizedPlace?.Id, displayName, authorizedPlace?.Region?.Name,
            IsValidLocation(authorizedPlace?.Location) ? authorizedPlace!.Location : null, routeVertexIndex);
    }

    /// <summary>Accepts custom geometry only when every semantic anchor mapping remains authoritative.</summary>
    private static (LineString Route, string Orientation)? ResolveCustomRoute(
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
        var forward = waypoints.Count == 0
            ? WithinLegacyThreshold(route.Coordinates[0], anchors[0].Location!.Coordinate)
                && WithinLegacyThreshold(route.Coordinates[^1], anchors[^1].Location!.Coordinate)
            : MatchesWaypointOrder(route, waypoints, anchors, reversed: false);
        var reversed = waypoints.Count == 0
            ? WithinLegacyThreshold(route.Coordinates[0], anchors[^1].Location!.Coordinate)
                && WithinLegacyThreshold(route.Coordinates[^1], anchors[0].Location!.Coordinate)
            : MatchesWaypointOrder(route, waypoints, anchors, reversed: true);
        if (forward && anchors[0].PlaceId == anchors[^1].PlaceId)
        {
            return ((LineString)route.Copy(), "forward");
        }
        if (forward == reversed) return null;
        return ((LineString)route.Copy(), forward ? "forward" : "reversed");
    }

    /// <summary>Checks strict #388 anchor indices in forward or reversed semantic order.</summary>
    private static bool MatchesWaypointOrder(LineString route, IReadOnlyList<SegmentWaypoint> waypoints,
        IReadOnlyList<ViewerJourneyAnchor> anchors, bool reversed)
    {
        var indices = new List<int>(waypoints.Count + 2) { reversed ? route.NumPoints - 1 : 0 };
        indices.AddRange(waypoints.Select(waypoint => waypoint.RouteVertexIndex ?? -1));
        indices.Add(reversed ? 0 : route.NumPoints - 1);
        return indices.Select((index, position) => index >= 0 && index < route.NumPoints
            && (position == 0 || (reversed ? index < indices[position - 1] : index > indices[position - 1]))
            && Matches(route.Coordinates[index], anchors[position].Location!.Coordinate)).All(value => value);
    }

    /// <summary>Uses the issue-approved 0.25 km threshold for zero-waypoint legacy endpoints.</summary>
    private static bool WithinLegacyThreshold(Coordinate left, Coordinate right)
    {
        const double earthRadiusKm = 6371.0088;
        var latitudeDelta = DegreesToRadians(right.Y - left.Y);
        var longitudeDelta = DegreesToRadians(right.X - left.X);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(DegreesToRadians(left.Y)) * Math.Cos(DegreesToRadians(right.Y))
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)) <= 0.25;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    /// <summary>Derives a non-persisted uppercase ASCII bijective base-26 label.</summary>
    private static string AlphabeticLabel(int position)
    {
        var remaining = position + 1;
        var label = string.Empty;
        while (remaining > 0)
        {
            remaining--;
            label = (char)('A' + remaining % 26) + label;
            remaining /= 26;
        }
        return label;
    }

    /// <summary>Returns whether a saved Place location is safe for map presentation.</summary>
    private static bool IsValidLocation(Point? point) => point != null
        && IsFinite(point.Coordinate)
        && point.X is >= -180 and <= 180
        && point.Y is >= -90 and <= 90;

    /// <summary>Allows neutral rendering only after the authorized anchors and geometry are safe.</summary>
    private static bool IsSafeNeutralRoute(LineString route, IReadOnlyList<ViewerJourneyAnchor> anchors) =>
        !route.IsEmpty && route.IsValid && route.NumPoints >= 2 && anchors.All(anchor => anchor.Location != null)
        && route.Coordinates.All(IsFinite);

    /// <summary>Compares one custom-route anchor using the persisted aggregate tolerance.</summary>
    private static bool Matches(Coordinate left, Coordinate right) =>
        Math.Abs(left.X - right.X) <= AnchorTolerance && Math.Abs(left.Y - right.Y) <= AnchorTolerance;

    /// <summary>Rejects non-finite coordinates rather than passing corrupt geometry to Leaflet.</summary>
    private static bool IsFinite(Coordinate coordinate) =>
        double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y);

    /// <summary>Creates bounded neutral output when semantic ordering cannot be trusted.</summary>
    private static ViewerSegmentJourney Degraded(Segment segment, int waypointCount, string message) =>
        new(segment.Id, [], null, waypointCount, 0, "ambiguous", message);
}
