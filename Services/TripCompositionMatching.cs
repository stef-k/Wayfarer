using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Deterministic review suggestions limited to 20 results; evidence is computed over all matching candidates. No score proves equivalence or writes mappings.</summary>
internal static class TripCompositionMatching
{
    internal static IReadOnlyList<object> Candidates(Trip source, Trip current, string side, string kind, Guid id,
        IReadOnlyList<TripCompositionMapping> mappings, string? search)
    {
        var target = TripCompositionSource.Target(source, kind, id);
        IEnumerable<object> candidates = kind switch
        {
            "region" => current.Regions.Where(r => r.Name != "Unassigned Places"),
            "place" => current.Regions.SelectMany(r => r.Places),
            "area" => current.Regions.SelectMany(r => r.Areas),
            "segment" => current.Segments,
            _ => throw TripCompositionSource.Invalid()
        };
        var mapped = mappings.Where(m => m.Source == side && m.Decision is not ("skip" or "destinationRemoved"))
            .GroupBy(m => (m.Kind, m.SourceId)).ToDictionary(g => g.Key, g => g.Select(m => m.DestinationId).ToHashSet());
        return candidates.Where(c => string.IsNullOrWhiteSpace(search) || Name(c).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(c => Evidence(target, c, mapped)).OrderByDescending(c => c.Score).ThenBy(c => c.DistanceKm ?? double.MaxValue)
            .ThenBy(c => c.Id).Take(20).Select(c => (object)new
            {
                destinationId = c.Id,
                name = c.Name,
                status = "possibleMatch",
                c.DistanceKm,
                c.SameName,
                c.Geometry,
                c.MappedChildren,
                c.MappedAnchors,
                c.SameMode,
                c.SameProfile
            }).ToArray();
    }

    private static Match Evidence(object source, object candidate, Dictionary<(string, Guid), HashSet<Guid>> mapped)
    {
        var sameName = string.Equals(Name(source).Trim(), Name(candidate).Trim(), StringComparison.OrdinalIgnoreCase);
        var distance = Distance(Point(source), Point(candidate));
        string? geometry = null;
        var children = 0;
        var anchors = false;
        bool? mode = null, profile = null;
        if (source is Area a && candidate is Area b)
            geometry = a.Geometry.EqualsTopologically(b.Geometry) ? "equal" : a.Geometry.Contains(b.Geometry) ? "contains"
                : b.Geometry.Contains(a.Geometry) ? "contained" : a.Geometry.Intersects(b.Geometry) ? "overlap" : "disjoint";
        if (source is Region r && candidate is Region s)
        {
            var ids = s.Places.Select(p => p.Id).Concat(s.Areas.Select(a => a.Id)).ToHashSet();
            children = r.Places.Count(p => mapped.TryGetValue(("place", p.Id), out var id) && id.Overlaps(ids))
                + r.Areas.Count(a => mapped.TryGetValue(("area", a.Id), out var id) && id.Overlaps(ids));
        }
        if (source is Segment x && candidate is Segment y)
        {
            bool Anchor(Guid? from, Guid? to) => from == null ? to == null : mapped.TryGetValue(("place", from.Value), out var id) && to.HasValue && id.Contains(to.Value);
            anchors = Anchor(x.FromPlaceId, y.FromPlaceId) && Anchor(x.ToPlaceId, y.ToPlaceId)
                && x.Waypoints.Count == y.Waypoints.Count && x.Waypoints.OrderBy(w => w.Position).Zip(y.Waypoints.OrderBy(w => w.Position))
                    .All(pair => Anchor(pair.First.PlaceId, pair.Second.PlaceId));
            mode = x.Mode == y.Mode; profile = x.TransportProfileId == y.TransportProfileId;
            geometry = x.RouteGeometry == null && y.RouteGeometry == null ? "fallback"
                : x.RouteGeometry?.EqualsExact(y.RouteGeometry) == true ? "equal" : "different";
        }
        var score = (sameName ? 10 : 0) + (geometry == "equal" ? 10 : 0) + children + (anchors ? 10 : 0)
            + (distance is < 1 ? 3 : distance is < 10 ? 1 : 0);
        return new(Id(candidate), Name(candidate), score, distance, sameName, geometry, children, anchors, mode, profile);
    }

    internal static string Name(object item) => item switch
    { Region r => r.Name, Place p => p.Name, Area a => a.Name, Segment s => s.Title ?? s.Mode, Trip t => t.Name, _ => "" };
    internal static Guid Id(object item) => item switch
    { Region r => r.Id, Place p => p.Id, Area a => a.Id, Segment s => s.Id, Trip t => t.Id, _ => Guid.Empty };
    private static Point? Point(object item) => item switch { Place p => p.Location, Region r => r.Center, _ => null };
    private static double? Distance(Point? a, Point? b)
    {
        if (a == null || b == null) return null;
        const double radians = Math.PI / 180;
        var dlat = (a.Y - b.Y) * radians;
        var dlon = (a.X - b.X) * radians;
        var h = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(a.Y * radians) * Math.Cos(b.Y * radians) * Math.Pow(Math.Sin(dlon / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }
    private sealed record Match(Guid Id, string Name, int Score, double? DistanceKm, bool SameName, string? Geometry,
        int MappedChildren, bool MappedAnchors, bool? SameMode, bool? SameProfile);
}
