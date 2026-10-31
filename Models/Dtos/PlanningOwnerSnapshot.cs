using Wayfarer.Models.Planning;
using NodaTime;

namespace Wayfarer.Models.Dtos;

/// <summary>Explicit private projection built inside the same authorized snapshot as the reusable graph.</summary>
public sealed record PlanningOwnerSnapshot(
    PlanningAggregate? Trip,
    IReadOnlyDictionary<Guid, PlanningAggregate?> Regions,
    IReadOnlyDictionary<Guid, PlanningAggregate?> Places,
    IReadOnlyDictionary<Guid, PlanningAggregate?> Areas,
    IReadOnlyDictionary<Guid, PlanningAggregate?> Segments,
    string ZoneRulesVersion)
{
    /// <summary>No separate query or public-graph hydration may construct this baseline.</summary>
    public static PlanningOwnerSnapshot FromTrip(Trip trip) => new(trip.Planning,
        trip.Regions.ToDictionary(r => r.Id, r => r.Planning),
        trip.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id, p => p.Planning),
        trip.Regions.SelectMany(r => r.Areas).ToDictionary(a => a.Id, a => a.Planning),
        trip.Segments.ToDictionary(s => s.Id, s => s.Planning), DateTimeZoneProviders.Tzdb.VersionId);
}
