using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Planning;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Fresh-snapshot advisory behavior, private setup reuse and independent coverage at the product service seam.</summary>
public sealed class PlanningCheckTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Alice's saved intentions and an unrelated rough date need no permanent assessment mode.</summary>
    private static EditorTripStateDto Graph()
    {
        PlanningAggregate Period(string start, string end, string kind) => new(1, [new(Guid.NewGuid(), new("period",
            Start: new("known", new("dateTime", "2027-03-02T" + start, "UTC")),
            End: new("known", new("dateTime", "2027-03-02T" + end, "UTC"))), Kind: kind)]);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "alice", Name = "Trip", Planning = new(1, [new(Guid.NewGuid(), new("at", new("month", "2027-03")))]) };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, Name = "Context", Planning = Period("09:00", "18:00", "window") };
        var museum = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, Name = "Museum", Location = new Point(0, 0), Planning = Period("10:00", "11:00", "presence") };
        var lunch = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, Name = "Lunch", Location = new Point(1, 1), Planning = Period("12:00", "13:00", "presence") };
        region.Places = [museum, lunch]; trip.Regions = [region];
        return EditorTripStateMapper.ToEditorState(trip, new Dictionary<Guid, IReadOnlyList<PlaceVisitEvent>>(), null!, null, null);
    }

    /// <summary>Exact keys are chosen from this snapshot, never inferred by labels or geography.</summary>
    private static (PlanningCheckResult Initial, string Museum, string Lunch, string Window) Initial(EditorTripStateDto graph)
    {
        var initial = PlanningCheckService.Run(graph, "alice", new(), Now);
        return (initial, initial.Inventory.Single(i => i.Name == "Museum").Key,
            initial.Inventory.Single(i => i.Name == "Lunch").Key, initial.Inventory.Single(i => i.Name == "Context").Key);
    }

    /// <summary>Context and movement links are explicit, including whether allowance/connection is already counted.</summary>
    private static PlanningCheckSetup Alice(string museum, string lunch, string window) => new()
    {
        Selection = [museum, lunch], SingleTraveller = true,
        Entries = new Dictionary<string, PlanningCheckEntryInput> { [museum] = new(Context: [window]), [lunch] = new(Context: [window]) },
        Transfers = [new(museum, lunch, EstimateMinutes: 30, Relevant: true, Allowance: "additional", AllowanceMinutes: 15, Connection: "none")]
    };

    /// <summary>Independent Unknowns cannot suppress completed supported checks, risk or exact context findings.</summary>
    [Fact]
    public void Alice_completes_scoped_checks_and_mixed_Trip_retains_rough_Unknown_without_mutation()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var before = JsonSerializer.Serialize(graph);
        var setup = Alice(museum, lunch, window);
        var scoped = PlanningCheckService.Run(graph, "alice", setup, Now);
        Assert.Contains(scoped.Findings, f => f.Check == "movement" && f.Result.Status == "completed" && f.Result.Minutes == 15);
        Assert.All(scoped.Findings.Where(f => f.Check is "context" or "physical"), f => Assert.Equal("completed", f.Result.Status));
        Assert.Equal(2, scoped.Inventory.Count(i => !i.Selected));
        var full = PlanningCheckService.Run(graph, "alice", setup with { Selection = null }, Now);
        Assert.Contains(full.Findings, f => f.Result.Status == "unknown" && f.Entries.Contains(full.Inventory.Single(i => i.Kind == "trip").Key));
        Assert.Contains(full.Findings, f => f.Result.Minutes == 15);
        Assert.Equal(before, JsonSerializer.Serialize(graph));
        Assert.Contains(full.Inventory, i => i.Entry.When.Value?.Precision == "month");
    }

    /// <summary>Unchanged inputs and label-only edits reuse; current bounds recompute without reasking context selection.</summary>
    [Fact]
    public void Reuse_tracks_relevant_dependencies_and_keeps_context_selection_across_window_time_changes()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var saved = PlanningCheckService.Run(graph, "alice", Alice(museum, lunch, window), Now);
        var again = PlanningCheckService.Run(graph, "alice", saved.Setup, Now);
        Assert.Empty(again.Invalidated);
        Assert.NotEmpty(again.Reused);
        var region = graph.RegionsById.Values.Single();
        var entry = region.Planning!.Entries.Single();
        var changed = entry with { Label = "Renamed", When = entry.When with { End = new("known", new("dateTime", "2027-03-02T10:30", "UTC")) } };
        graph = graph with { RegionsById = new Dictionary<Guid, EditorRegionDto> { [region.Id] = region with { Planning = new(1, [changed]) } } };
        var reviewed = PlanningCheckService.Run(graph, "alice", saved.Setup, Now);
        Assert.Equal(new[] { window }, reviewed.Setup.Entries[museum].Context);
        Assert.Contains(reviewed.Findings, f => f.Result.Status == "mismatch" && f.Result.Minutes == 30);
        Assert.Contains(reviewed.Findings, f => f.Result.Status == "mismatch" && f.Result.Minutes == 60);
        Assert.Empty(reviewed.Invalidated);
    }

    /// <summary>New entries are included by default; explicit selection and deleted/reparented links stay honest.</summary>
    [Fact]
    public void Membership_changes_preserve_inventory_and_reopen_only_affected_links()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var saved = PlanningCheckService.Run(graph, "alice", Alice(museum, lunch, window) with { Selection = null }, Now);
        var place = graph.PlacesById.Values.First();
        var added = place with { Id = Guid.NewGuid(), Name = "New rough entry", Planning = new(1, [new(Guid.NewGuid(), new("at", new("date", "2027-03-03")))]) };
        var places = graph.PlacesById.ToDictionary(); places.Add(added.Id, added);
        var expanded = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", saved.Setup, Now);
        Assert.True(expanded.Inventory.Single(i => i.TargetId == added.Id).Selected);
        Assert.False(expanded.Setup.SingleTraveller);
        Assert.Contains(expanded.Findings, f => f.Result.Status == "unknown" && f.Entries.Contains(expanded.Inventory.Single(i => i.TargetId == added.Id).Key));
        var museumPlace = places.Values.Single(p => p.Name == "Museum");
        places[museumPlace.Id] = museumPlace with { RegionId = Guid.NewGuid() };
        var moved = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", saved.Setup, Now);
        Assert.Null(moved.Setup.Entries[museum].Context);
        Assert.False(moved.Setup.Transfers.Single().Relevant);
        places.Remove(museumPlace.Id);
        var deleted = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", saved.Setup, Now);
        Assert.DoesNotContain(deleted.Inventory, i => i.Key == museum);
        Assert.Empty(deleted.Setup.Transfers);
        Assert.Contains(deleted.Invalidated, value => value.Contains("deleted"));
    }

    /// <summary>Changed location/route/estimate evidence reopens relevance and extras, while the known gap remains available.</summary>
    [Fact]
    public void Changed_route_location_and_estimate_invalidate_inputs_and_what_if_expires()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var segmentId = Guid.NewGuid();
        var segment = new EditorSegmentDto(segmentId, graph.TripId, graph.PlacesById.Values.First().Id, graph.PlacesById.Values.Last().Id,
            [], [], "walk", null, false, 1, 30, "Manual", "", null, null, "", 0, new(true, true, true, true, true, false, false));
        graph = graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = segment } };
        var setup = Alice(museum, lunch, window);
        setup = setup with { Transfers = [setup.Transfers[0] with { SegmentId = segmentId, WhatIfRelease = new("dateTime", "2027-03-02T11:00", "UTC") }] };
        var saved = PlanningCheckService.Run(graph, "alice", setup, Now);
        var expired = PlanningCheckService.Run(graph, "alice", saved.Setup, Now.AddMinutes(31));
        Assert.Null(expired.Setup.Transfers.Single().WhatIfRelease);
        Assert.True(expired.Setup.Transfers.Single().Relevant);
        foreach (var changed in new[] { segment with { Mode = "bus" }, segment with { EstimatedDurationMinutes = 45 } })
        {
            var reviewed = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = changed } }, "alice", saved.Setup, Now);
            Assert.False(reviewed.Setup.Transfers.Single().Relevant);
            Assert.Equal("unknown", reviewed.Setup.Transfers.Single().Allowance);
            Assert.Contains(reviewed.Findings, f => f.Check == "movement" && f.Result.Status == "unknown");
        }
        var place = graph.PlacesById.Values.First();
        var places = graph.PlacesById.ToDictionary(); places[place.Id] = place with { Location = new(25, 35) };
        var relocated = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", saved.Setup, Now);
        Assert.False(relocated.Setup.Transfers.Single().Relevant);
    }

    /// <summary>Stay/window overlap is legitimate; arbitrary IDs do not supply incompatible-location evidence.</summary>
    [Fact]
    public void Physical_presence_needs_explicit_premises_and_stays_are_not_occupied_time()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var place = graph.PlacesById.Values.Single(p => p.Name == "Lunch");
        var when = place.Planning!.Entries.Single().When;
        var overlap = place with { Planning = new(1, [place.Planning.Entries[0] with { When = when with { Start = new("known", new("dateTime", "2027-03-02T10:30", "UTC")) } }]) };
        var places = graph.PlacesById.ToDictionary(); places[place.Id] = overlap;
        graph = graph with { PlacesById = places };
        var setup = Alice(museum, lunch, window);
        var noPremise = PlanningCheckService.Run(graph, "alice", setup, Now);
        Assert.DoesNotContain(noPremise.Findings, f => f.Check == "physical" && f.Result.Status == "contradiction");
        setup = setup with { Incompatible = new Dictionary<string, string?> { [string.Join('|', new[] { museum, lunch }.Order())] = null } };
        Assert.Contains(PlanningCheckService.Run(graph, "alice", setup, Now).Findings, f => f.Check == "physical" && f.Result.Status == "contradiction");
        places[place.Id] = overlap with { Planning = new(1, [overlap.Planning!.Entries[0] with { Kind = "stay" }]) };
        Assert.DoesNotContain(PlanningCheckService.Run(graph, "alice", setup, Now).Findings, f => f.Check == "physical");
    }

    /// <summary>Check-local occurrence choices resolve only the affected fold and expire when that clock changes.</summary>
    [Fact]
    public void Local_fold_choice_is_not_a_planning_edit_and_revalidates_its_clock()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var region = graph.RegionsById.Values.Single();
        var context = region.Planning!.Entries[0] with { When = new("period",
            Start: new("known", new("dateTime", "2027-10-31T00:00", "UTC")), End: new("known", new("dateTime", "2027-10-31T01:00", "UTC"))) };
        var place = graph.PlacesById.Values.Single(p => p.Name == "Museum");
        var visit = place.Planning!.Entries[0] with { When = new("at", new("dateTime", "2027-10-31T01:30", "Europe/London")), AllocationMinutes = 15 };
        var places = graph.PlacesById.ToDictionary(); places[place.Id] = place with { Planning = new(1, [visit]) };
        graph = graph with { PlacesById = places, RegionsById = new Dictionary<Guid, EditorRegionDto> { [region.Id] = region with { Planning = new(1, [context]) } } };
        var setup = new PlanningCheckSetup { Selection = [museum], Entries = new Dictionary<string, PlanningCheckEntryInput> { [museum] = new(Context: [window]) } };
        Assert.Equal("possible", PlanningCheckService.Run(graph, "alice", setup, Now).Findings.Single(f => f.Check == "context").Result.Status);
        setup = setup with { Entries = new Dictionary<string, PlanningCheckEntryInput> { [museum] = new(Context: [window], Occurrences: new Dictionary<string, int?> { ["value"] = 3600 }) } };
        var resolved = PlanningCheckService.Run(graph, "alice", setup, Now);
        Assert.Equal("completed", resolved.Findings.Single(f => f.Check == "context").Result.Status);
        Assert.Null(graph.PlacesById[place.Id].Planning!.Entries[0].When.Value!.SelectedOffsetSeconds);
        places[place.Id] = place with { Planning = new(1, [visit with { When = new("at", new("dateTime", "2027-10-31T01:45", "Europe/London")) }]) };
        var changed = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", resolved.Setup, Now);
        Assert.Null(changed.Setup.Entries[museum].Occurrences);
        Assert.Equal("possible", changed.Findings.Single(f => f.Check == "context").Result.Status);
    }
}
