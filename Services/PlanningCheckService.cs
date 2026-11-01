using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodaTime;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

/// <summary>One owner snapshot becomes bounded advisory checks. No writes, route providers, history or public projections.</summary>
public static class PlanningCheckService
{
    /// <summary>Revalidates only affected check inputs, retaining all inventory and independent findings.</summary>
    public static PlanningCheckResult Run(EditorTripStateDto graph, string ownerId, PlanningCheckSetup setup, DateTimeOffset now)
    {
        Validate(setup);
        var items = Inventory(graph, setup.Selection);
        var byKey = items.ToDictionary(i => i.Key);
        List<string> reused = [], invalidated = [];
        var inputs = RevalidateEntries(items, setup.Entries, reused, invalidated);
        var selected = items.Where(i => i.Selected).ToArray();
        var travellerDependency = Fingerprint(selected.Select(i => new { i.Key, i.ParentId, Kind = Effective(i, inputs).Kind }));
        var single = setup.SingleTraveller;
        if (single && setup.TravellerDependency != null && setup.TravellerDependency != travellerDependency)
        { single = false; invalidated.Add("Single-traveller premise: selected participants changed."); }
        else if (single && setup.TravellerDependency != null) reused.Add("Single-traveller premise");
        List<PlanningCheckFinding> findings = [];
        foreach (var item in selected)
        {
            var entry = Effective(item, inputs);
            var input = inputs.GetValueOrDefault(item.Key);
            if (entry.Kind == null)
                Add(findings, "meaning", [item.Key], new("unknown", "Meaning is unspecified. Clarify only for this check, or leave unresolved."));
            else if (entry.Kind is "stay" or "window")
                Add(findings, "meaning", [item.Key], new("completed", "Stay availability/planning windows do not imply continuous physical presence; overlaps are legitimate."));
            else if (entry.Kind == "travel")
                Add(findings, "meaning", [item.Key], new("unknown", "Journey intention is retained; choose related movement entries to assess travel. Via Places are not timed stops."));
            if (item.Kind == "trip") continue;
            var context = input?.Context ?? [];
            var windows = context.Select(key => byKey.GetValueOrDefault(key)).ToArray();
            var comparison = context.Count == 0 ? new PlanningComparison("unknown", "Choose relevant parent windows or leave context unresolved; ownership alone is not a time constraint.")
                : windows.Any(w => w == null || !item.ParentWindows.Contains(w.Key) || Effective(w, inputs).Kind != "window")
                    ? new PlanningComparison("unknown", "A selected context is no longer a relevant parent window; review the selection.")
                    : PlanningComparisons.Containment(PlanningIntervals.Resolve(entry, input?.Zone), windows.Select(w => PlanningIntervals.Resolve(Effective(w!, inputs), inputs.GetValueOrDefault(w!.Key)?.Zone)).ToArray());
            Add(findings, "context", new[] { item.Key }.Concat(context).ToArray(), comparison);
        }
        var incompatible = Physical(selected, inputs, setup.Incompatible, single, findings, reused, invalidated);
        var transfers = setup.Transfers.Where(t => byKey.ContainsKey(t.From) && byKey.ContainsKey(t.To)).Select(t =>
            Transfer(graph, t, byKey, inputs, now, findings, reused, invalidated)).ToArray();
        if (transfers.Length != setup.Transfers.Count) invalidated.Add("Movement links to deleted entries were removed.");
        if (selected.Count(i => Effective(i, inputs).Kind == "presence") > 1 && !transfers.Any(t => byKey[t.From].Selected && byKey[t.To].Selected))
            Add(findings, "movement", selected.Where(i => Effective(i, inputs).Kind == "presence").Select(i => i.Key).ToArray(), new("unknown", "Movement has no selected pair. Choose release → arrival entries; display order does not establish travel."));
        foreach (var item in items.Where(i => !i.Selected))
            Add(findings, "coverage", [item.Key], new("excluded", "Explicitly outside this temporary selection; remains saved and unassessed."));
        var current = setup with { Entries = inputs, SingleTraveller = single, TravellerDependency = travellerDependency,
            Transfers = transfers, Incompatible = incompatible };
        return new("planning-check-1", ownerId, graph.TripId, DateTimeZoneProviders.Tzdb.VersionId, items, findings, current, reused, invalidated);
    }

    /// <summary>Entity-owned entry keys cannot join other Trips; parent lists are candidates, never inferred constraints.</summary>
    private static List<PlanningCheckItem> Inventory(EditorTripStateDto graph, IReadOnlyList<string>? selection)
    {
        List<PlanningCheckItem> items = [];
        void Append(string kind, Guid id, Guid parent, string name, PlanningAggregate? aggregate, object? location)
        {
            foreach (var entry in aggregate?.Entries ?? [])
            {
                var key = $"{kind}/{id}/{entry.Id}";
                var dependency = Fingerprint(new { kind, id, parent, entry.When, entry.Kind, entry.AllocationMinutes, location, Rules = DateTimeZoneProviders.Tzdb.VersionId });
                items.Add(new(key, kind, id, parent, name, entry, selection == null || selection.Contains(key), [], dependency));
            }
        }
        Append("trip", graph.TripId, graph.TripId, graph.Metadata.Name, graph.Metadata.Planning, null);
        foreach (var r in graph.RegionsById.Values) Append("region", r.Id, graph.TripId, r.Name, r.Planning, r.Center);
        foreach (var p in graph.PlacesById.Values) Append("place", p.Id, p.RegionId, p.Name, p.Planning, p.Location);
        foreach (var a in graph.AreasById.Values) Append("area", a.Id, a.RegionId, a.Name, a.Planning, a.Geometry);
        foreach (var s in graph.SegmentsById.Values) Append("segment", s.Id, graph.TripId, $"Journey {s.Mode}", s.Planning, new { s.FromPlaceId, s.ToPlaceId, s.WaypointPlaceIds, s.Route, s.EffectiveRoute });
        // Candidates include unclassified parent entries, which can be explicitly interpreted as windows.
        return items.Select(item => item with { ParentWindows = items.Where(w => w.Key != item.Key && w.TargetId != item.TargetId &&
            (w.Kind == "trip" || w.Kind == "region" && w.TargetId == item.ParentId)).Select(w => w.Key).ToArray() }).ToList();
    }

    /// <summary>Bounds changes recompute containment without reasking unchanged context membership; labels do not invalidate.</summary>
    private static Dictionary<string, PlanningCheckEntryInput> RevalidateEntries(IReadOnlyList<PlanningCheckItem> items,
        IReadOnlyDictionary<string, PlanningCheckEntryInput> prior, List<string> reused, List<string> invalidated)
    {
        Dictionary<string, PlanningCheckEntryInput> current = [];
        foreach (var item in items)
        {
            if (!prior.TryGetValue(item.Key, out var input)) continue;
            var contextDependency = Fingerprint(new { item.Key, item.ParentId, Windows = (input.Context ?? []).Select(k => {
                var w = items.SingleOrDefault(i => i.Key == k); return new { Key = k, w?.ParentId, w?.Entry.Kind }; }) });
            if (input.Dependency != null && input.Dependency != item.Dependency)
            {
                input = input with { Kind = null, Zone = null, AllocationMinutes = null };
                invalidated.Add($"{item.Name}: changed entry/location/rules reopened interpretations.");
            }
            else if (input.Dependency != null) reused.Add($"{item.Name}: entry interpretations");
            if (input.ContextDependency != null && input.ContextDependency != contextDependency)
            { input = input with { Context = null }; invalidated.Add($"{item.Name}: context membership changed."); }
            else if (input.Context is { Count: > 0 } && input.ContextDependency != null) reused.Add($"{item.Name}: selected parent windows (coverage recomputed)");
            current[item.Key] = input with { Dependency = item.Dependency, ContextDependency = contextDependency };
        }
        if (prior.Keys.Any(k => !items.Any(i => i.Key == k))) invalidated.Add("Inputs for removed entries were discarded.");
        return current;
    }

    /// <summary>Meaning/dwell fill only absent saved facts; interpreted zone never removes a saved endpoint zone or fold.</summary>
    private static PlanningEntry Effective(PlanningCheckItem item, IReadOnlyDictionary<string, PlanningCheckEntryInput> inputs)
    {
        var input = inputs.GetValueOrDefault(item.Key);
        return item.Entry with { Kind = item.Entry.Kind ?? input?.Kind, AllocationMinutes = item.Entry.AllocationMinutes ?? input?.AllocationMinutes };
    }

    /// <summary>Joint occupation is bounded; overflow is visible Unknown, never silently certified.</summary>
    private static Dictionary<string, string?> Physical(PlanningCheckItem[] selected, IReadOnlyDictionary<string, PlanningCheckEntryInput> inputs,
        IReadOnlyDictionary<string, string?> prior, bool single, List<PlanningCheckFinding> findings, List<string> reused, List<string> invalidated)
    {
        Dictionary<string, string?> current = [];
        var presence = selected.Where(i => Effective(i, inputs).Kind == "presence").ToArray();
        var count = 0;
        for (var a = 0; a < presence.Length; a++)
        {
            for (var b = a + 1; b < presence.Length; b++)
            {
                if (++count > 2048) continue;
                var first = presence[a]; var second = presence[b];
                var key = string.Join('|', new[] { first.Key, second.Key }.Order());
                var dependency = Fingerprint(new { first.Dependency, Second = second.Dependency });
                var incompatible = prior.TryGetValue(key, out var old) && (old == null || old == dependency);
                if (incompatible) { current[key] = dependency; if (old != null) reused.Add("Incompatible-location assessment"); }
                else if (old != null) invalidated.Add("Changed presence/location reopened incompatible-location assessment.");
                Add(findings, "physical", [first.Key, second.Key], PlanningComparisons.Physical(
                    PlanningIntervals.Resolve(Effective(first, inputs), inputs.GetValueOrDefault(first.Key)?.Zone),
                    PlanningIntervals.Resolve(Effective(second, inputs), inputs.GetValueOrDefault(second.Key)?.Zone), single, incompatible));
            }
        }
        if (count > 2048) Add(findings, "physical", presence.Select(i => i.Key).ToArray(), new("unknown", $"{count - 2048} further physical pairs are unassessed beyond the 2048-pair bound. Narrow the displayed selection explicitly."));
        return current;
    }

    /// <summary>Directional route/location evidence invalidates estimates and extras; time-sensitive releases have a short lifetime.</summary>
    private static PlanningCheckTransfer Transfer(EditorTripStateDto graph, PlanningCheckTransfer transfer,
        IReadOnlyDictionary<string, PlanningCheckItem> items, IReadOnlyDictionary<string, PlanningCheckEntryInput> inputs,
        DateTimeOffset now, List<PlanningCheckFinding> findings, List<string> reused, List<string> invalidated)
    {
        var from = items[transfer.From]; var to = items[transfer.To];
        var segment = transfer.SegmentId.HasValue ? graph.SegmentsById.GetValueOrDefault(transfer.SegmentId.Value) : null;
        object? Location(PlanningCheckItem item) => item.Kind == "place" ? graph.PlacesById[item.TargetId].Location
            : item.Kind == "area" ? graph.AreasById[item.TargetId].Geometry : (object?)item.ParentId;
        var dependency = Fingerprint(new { from.Key, from.ParentId, FromLocation = Location(from), To = to.Key, ToParent = to.ParentId, ToLocation = Location(to),
            Segment = segment == null ? null : new { segment.Id, segment.FromPlaceId, segment.ToPlaceId, segment.WaypointPlaceIds, segment.WaypointRouteVertexIndices,
                segment.Mode, segment.TransportProfileId, segment.Route, segment.EffectiveRoute, segment.EstimatedDurationMinutes, segment.EstimatedDurationSource } });
        if (transfer.Dependency != null && transfer.Dependency != dependency)
        {
            transfer = transfer with { Relevant = false, Allowance = "unknown", Connection = "unknown" };
            invalidated.Add("Movement route/location/estimate changed: confirm relevance and extras again.");
        }
        else if (transfer.Dependency != null) reused.Add("Movement estimate relevance and allowance treatment");
        var whatIfDependency = Fingerprint(new { from.Dependency, Rules = DateTimeZoneProviders.Tzdb.VersionId });
        if (transfer.WhatIfRelease != null && transfer.WhatIfAt != null && (transfer.WhatIfDependency != whatIfDependency
            || now - transfer.WhatIfAt > TimeSpan.FromMinutes(30) || now.UtcDateTime.Date != transfer.WhatIfAt.Value.UtcDateTime.Date || transfer.WhatIfAt > now))
        { transfer = transfer with { WhatIfRelease = null, WhatIfAt = null }; invalidated.Add("What-if release expired or its timing evidence changed."); }
        transfer = transfer with { Dependency = dependency, WhatIfDependency = whatIfDependency,
            WhatIfAt = transfer.WhatIfRelease != null ? transfer.WhatIfAt ?? now : null };
        if (!from.Selected || !to.Selected)
        { Add(findings, "movement", [from.Key, to.Key], new("excluded", "Movement pair is outside this selection.")); return transfer; }
        var estimate = transfer.Relevant ? transfer.SegmentId.HasValue ? segment?.EstimatedDurationMinutes : transfer.EstimateMinutes : null;
        var allowance = Extra(transfer.Allowance, transfer.AllowanceMinutes);
        var connection = Extra(transfer.Connection, transfer.ConnectionMinutes);
        var comparisons = PlanningComparisons.Movement(PlanningIntervals.Endpoint(Effective(from, inputs), true, inputs.GetValueOrDefault(from.Key)?.Zone, transfer.WhatIfRelease),
            PlanningIntervals.Endpoint(Effective(to, inputs), false, inputs.GetValueOrDefault(to.Key)?.Zone), estimate,
            allowance + connection, allowance != null && connection != null);
        foreach (var comparison in comparisons) Add(findings, "movement", [from.Key, to.Key], comparison);
        return transfer;
    }

    /// <summary>Included or explicitly unnecessary extras are counted zero additional times, never silently defaulted.</summary>
    private static double? Extra(string treatment, double? minutes) => treatment is "none" or "included" ? 0 : treatment == "additional" ? minutes : null;

    /// <summary>Stable per-review IDs permit collapsing a finding without removing it from the summary.</summary>
    private static void Add(List<PlanningCheckFinding> list, string check, IReadOnlyList<string> entries, PlanningComparison comparison) =>
        list.Add(new($"{check}/{list.Count}", check, entries, comparison));

    /// <summary>Private content binding only, not a bearer, certificate or mutable save original.</summary>
    private static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, PlanningCodec.Json))));

    /// <summary>Rejects oversized or malformed check-only inputs without changing saved content.</summary>
    private static void Validate(PlanningCheckSetup setup)
    {
        if (setup.Entries == null || setup.Transfers == null || setup.Incompatible == null || setup.Entries.Count > 512
            || setup.Transfers.Count > 64 || setup.Incompatible.Count > 2048 || setup.Selection?.Count > 4096)
            throw new PlanningValidationException("Check setup exceeds its bounded scope; narrow the selection.");
        foreach (var input in setup.Entries.Values)
            if (input == null || input.Kind != null && input.Kind is not ("window" or "presence" or "stay" or "travel")
                || input.AllocationMinutes < 0 || input.Context?.Count > 128 || input.Zone?.Length > 100)
                throw new PlanningValidationException("Choose supported check interpretations and nonnegative elapsed minutes.");
        foreach (var t in setup.Transfers)
        {
            if (t == null || string.IsNullOrEmpty(t.From) || string.IsNullOrEmpty(t.To) || t.From == t.To
                || !ValidMinutes(t.EstimateMinutes) || !ValidMinutes(t.AllowanceMinutes) || !ValidMinutes(t.ConnectionMinutes)
                || !ValidTreatment(t.Allowance) || !ValidTreatment(t.Connection))
                throw new PlanningValidationException("Choose distinct movement entries, supported treatment and nonnegative finite minutes.");
            if (t.WhatIfRelease != null) PlanningValidation.Validate(new(1, [new(Guid.NewGuid(), new("at", t.WhatIfRelease))]), "trip");
        }
    }

    /// <summary>Unknown is null, never NaN, infinity or a negative duration.</summary>
    private static bool ValidMinutes(double? value) => value == null || double.IsFinite(value.Value) && value >= 0;
    /// <summary>There is no invented default connection or buffer.</summary>
    private static bool ValidTreatment(string treatment) => treatment is "unknown" or "none" or "included" or "additional";
}
