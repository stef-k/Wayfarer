using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

/// <summary>The complete reusable native graph, shared by export and replacement review.</summary>
internal static class NativeTripGraph
{
    /// <summary>Loads routes, holes, tags, measurements and planning in the caller's transaction.</summary>
    internal static IQueryable<Trip> Query(ApplicationDbContext db) => db.Trips
        .Include(t => t.Regions).ThenInclude(r => r.Places)
        .Include(t => t.Regions).ThenInclude(r => r.Areas)
        .Include(t => t.Segments).ThenInclude(s => s.FromPlace)
        .Include(t => t.Segments).ThenInclude(s => s.ToPlace)
        .Include(t => t.Segments).ThenInclude(s => s.Waypoints).ThenInclude(w => w.Place)
        .Include(t => t.Segments).ThenInclude(s => s.TransportProfile)
        .Include(t => t.Tags).AsNoTrackingWithIdentityResolution().AsSplitQuery();

    /// <summary>Type and identity are the only planning attachment keys; names only label the review.</summary>
    internal static IEnumerable<(string Kind, Guid Id, Guid Parent, string Name, IPlanningTarget Target)> Targets(Trip trip)
    {
        yield return ("trip", trip.Id, trip.Id, trip.Name, trip);
        foreach (var region in trip.Regions)
        {
            yield return ("region", region.Id, trip.Id, region.Name, region);
            foreach (var place in region.Places) yield return ("place", place.Id, region.Id, place.Name, place);
            foreach (var area in region.Areas) yield return ("area", area.Id, region.Id, area.Name, area);
        }
        foreach (var segment in trip.Segments) yield return ("segment", segment.Id, trip.Id, segment.Mode, segment);
    }
}
