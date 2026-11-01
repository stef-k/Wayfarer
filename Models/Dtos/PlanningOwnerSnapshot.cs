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
    string ZoneRulesVersion,
    IReadOnlyDictionary<string, IReadOnlyList<PlanningDiagnostic>> Diagnostics)
{
    /// <summary>No separate query or public-graph hydration may construct this baseline.</summary>
    public static PlanningOwnerSnapshot FromTrip(Trip trip) => new(trip.Planning,
        trip.Regions.ToDictionary(r => r.Id, r => r.Planning),
        trip.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id, p => p.Planning),
        trip.Regions.SelectMany(r => r.Areas).ToDictionary(a => a.Id, a => a.Planning),
        trip.Segments.ToDictionary(s => s.Id, s => s.Planning), DateTimeZoneProviders.Tzdb.VersionId, BuildDiagnostics(trip));

    /// <summary>Reports current-rule limitations without changing historical authored values.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<PlanningDiagnostic>> BuildDiagnostics(Trip trip)
    {
        var values = new[] { (Kind: "trip", Id: trip.Id, Value: trip.Planning) }
            .Concat(trip.Regions.Select(r => (Kind: "region", Id: r.Id, Value: r.Planning)))
            .Concat(trip.Regions.SelectMany(r => r.Places).Select(p => (Kind: "place", Id: p.Id, Value: p.Planning)))
            .Concat(trip.Regions.SelectMany(r => r.Areas).Select(a => (Kind: "area", Id: a.Id, Value: a.Planning)))
            .Concat(trip.Segments.Select(s => (Kind: "segment", Id: s.Id, Value: s.Planning)));
        return values.ToDictionary(v => $"{v.Kind}/{v.Id}", v => PlanningValidation.Diagnostics(v.Value, v.Kind));
    }
}
