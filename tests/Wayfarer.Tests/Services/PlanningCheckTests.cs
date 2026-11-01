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

    /// <summary>An explicit check-only arrive-by target is independent of the saved intended arrival.</summary>
    [Fact]
    public void Check_local_deadline_reports_risk_without_rewriting_saved_intended_arrival()
    {
        var graph = Graph();
        var destination = graph.PlacesById.Values.Single(p => p.Name == "Lunch");
        var entry = destination.Planning!.Entries[0] with { When = new("period",
            Start: new("known", new("dateTime", "2027-03-02T13:00", "UTC")),
            End: new("known", new("dateTime", "2027-03-02T14:00", "UTC"))) };
        var places = graph.PlacesById.ToDictionary();
        places[destination.Id] = destination with { Planning = new(1, [entry]) };
        graph = graph with { PlacesById = places };
        var (_, museum, lunch, window) = Initial(graph);
        var setup = Alice(museum, lunch, window);
        // Exercise the strict product wire contract, including the formerly missing approved input.
        var wire = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(setup, PlanningCodec.Json))!;
        var transfer = wire["transfers"]![0]!;
        transfer["estimateMinutes"] = 90;
        transfer["allowance"] = "none";
        transfer["arrivalDeadline"] = JsonSerializer.SerializeToNode(new PlanningValue("dateTime", "2027-03-02T12:00", "UTC"), PlanningCodec.Json);
        setup = JsonSerializer.Deserialize<PlanningCheckSetup>(wire.ToJsonString(), PlanningCodec.Json)!;
        var before = JsonSerializer.Serialize(graph);
        var result = PlanningCheckService.Run(graph, "alice", setup, Now);
        Assert.Contains(result.Findings, f => f.Check == "movement" && f.Result.Status == "risk" && f.Result.Minutes == 30 && f.Result.Message.Contains("Arrive by"));
        Assert.Contains(result.Findings, f => f.Check == "movement" && f.Result.Status == "completed" && f.Result.Minutes == 30 && f.Result.Message.Contains("Intended arrival"));
        var later = result.Setup with { Transfers = [result.Setup.Transfers[0] with { ArrivalDeadline = new("dateTime", "2027-03-02T12:30", "UTC") }] };
        var changed = PlanningCheckService.Run(graph, "alice", later, Now);
        Assert.Empty(changed.Invalidated);
        Assert.True(changed.Setup.Transfers[0].Relevant);
        Assert.Contains(changed.Findings, f => f.Result.Status == "completed" && f.Result.Minutes == 0 && f.Result.Message.Contains("Arrive by"));
        var removed = PlanningCheckService.Run(graph, "alice", later with { Transfers = [later.Transfers[0] with { ArrivalDeadline = null }] }, Now);
        Assert.DoesNotContain(removed.Findings, f => f.Result.Message.Contains("Arrive by"));
        Assert.Contains(removed.Findings, f => f.Result.Status == "completed" && f.Result.Minutes == 30 && f.Result.Message.Contains("Intended arrival"));
        Assert.Equal(before, JsonSerializer.Serialize(graph));
    }

    /// <summary>End-only targets support an explicit local deadline; coarse, unzoned and unresolved fold deadlines stay Unknown.</summary>
    [Fact]
    public void End_only_never_supplies_an_implicit_deadline_and_local_values_use_existing_validation()
    {
        var graph = Graph();
        var destination = graph.PlacesById.Values.Single(p => p.Name == "Lunch");
        var entry = destination.Planning!.Entries[0] with { When = new("period", End: new("known", new("dateTime", "2027-03-02T13:00", "UTC"))) };
        var places = graph.PlacesById.ToDictionary(); places[destination.Id] = destination with { Planning = new(1, [entry]) };
        graph = graph with { PlacesById = places };
        var (_, museum, lunch, window) = Initial(graph);
        var setup = Alice(museum, lunch, window);
        var transfer = setup.Transfers[0] with { EstimateMinutes = 90, Allowance = "none" };
        var noDeadline = PlanningCheckService.Run(graph, "alice", setup with { Transfers = [transfer] }, Now);
        Assert.Equal("unknown", Assert.Single(noDeadline.Findings.Where(f => f.Check == "movement")).Result.Status);
        var known = PlanningCheckService.Run(graph, "alice", setup with { Transfers = [transfer with { ArrivalDeadline = new("dateTime", "2027-03-02T12:00", "UTC") }] }, Now);
        Assert.Contains(known.Findings, f => f.Result.Status == "risk" && f.Result.Minutes == 30 && f.Result.Message.Contains("Arrive by"));
        foreach (var deadline in new PlanningValue[] { new("date", "2027-03-02"), new("dateTime", "2027-03-02T12:00"), new("dateTime", "2027-10-31T01:30", "Europe/London") })
        {
            var unknown = PlanningCheckService.Run(graph, "alice", setup with { Transfers = [transfer with { ArrivalDeadline = deadline }] }, Now);
            Assert.Equal("unknown", Assert.Single(unknown.Findings.Where(f => f.Result.Message.Contains("Arrive by"))).Result.Status);
        }
        Assert.Throws<PlanningValidationException>(() => PlanningCheckService.Run(graph, "alice", setup with {
            Transfers = [transfer with { ArrivalDeadline = new("dateTime", "2027-03-28T01:30", "Europe/London") }] }, Now));
    }

    /// <summary>Reconfirmation binds the displayed 45-minute source; a second change reopens only its assessment.</summary>
    [Fact]
    public void Movement_source_descriptors_and_confirmation_share_the_current_snapshot()
    {
        var graph = Graph();
        var (_, museum, lunch, window) = Initial(graph);
        var from = graph.PlacesById.Values.Single(p => p.Name == "Museum");
        var to = graph.PlacesById.Values.Single(p => p.Name == "Lunch");
        var segmentId = Guid.NewGuid();
        var segment = new EditorSegmentDto(segmentId, graph.TripId, from.Id, to.Id, [], [], "walk", null, false, 1, 30, "Manual", "", null, null, "", 0, new(true, true, true, true, true, false, false));
        graph = graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = segment } };
        var setup = Alice(museum, lunch, window);
        var initial = PlanningCheckService.Run(graph, "alice", setup with { Transfers = [setup.Transfers[0] with { SegmentId = segmentId }] }, Now);
        graph = graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = segment with { EstimatedDurationMinutes = 45 } } };
        var displayed = PlanningCheckService.Run(graph, "alice", initial.Setup, Now);
        Assert.False(displayed.Setup.Transfers[0].Relevant);
        Assert.Contains("45 min", displayed.EstimateSources.Single().Description);
        Assert.Contains("Museum (0, 0) → Lunch (1, 1)", displayed.MovementEvidence.Single().Description);
        var reconfirmed = displayed.Setup with { Transfers = [displayed.Setup.Transfers[0] with { Relevant = true,
            Dependency = displayed.MovementEvidence.Single().Dependency, Allowance = "additional", AllowanceMinutes = 15, Connection = "none" }] };
        var accepted = PlanningCheckService.Run(graph, "alice", reconfirmed, Now);
        Assert.True(accepted.Setup.Transfers[0].Relevant);
        Assert.Contains(accepted.Findings, f => f.Check == "movement" && f.Result.Status == "completed" && f.Result.Minutes == 0);
        Assert.Empty(PlanningCheckService.Run(graph, "alice", accepted.Setup, Now).Invalidated);
        foreach (var newer in new[] { segment with { EstimatedDurationMinutes = 60 }, segment with { EstimatedDurationMinutes = 45, Mode = "bus" },
            segment with { EstimatedDurationMinutes = 45, FromPlaceId = to.Id, ToPlaceId = from.Id } })
        {
            var changed = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = newer } }, "alice", reconfirmed, Now);
            Assert.False(changed.Setup.Transfers[0].Relevant);
            Assert.Equal("unknown", changed.Setup.Transfers[0].Allowance);
            Assert.True(changed.Setup.SingleTraveller);
            Assert.Equal(new[] { window }, changed.Setup.Entries[lunch].Context);
            Assert.NotEqual(displayed.MovementEvidence[0].Dependency, changed.MovementEvidence[0].Dependency);
            Assert.Contains(newer.Mode, changed.EstimateSources[0].Description);
        }
    }

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
        foreach (var changed in new[] { segment with { Mode = "bus" }, segment with { EstimatedDurationMinutes = 45 },
            segment with { Route = JsonSerializer.SerializeToElement(new { type = "LineString", coordinates = new[] { new[] { 2, 2 }, new[] { 3, 3 } } }) } })
        {
            var editedAllowance = saved.Setup with { Transfers = [saved.Setup.Transfers[0] with { AllowanceMinutes = 10 }] };
            var reviewed = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = changed } }, "alice", editedAllowance, Now);
            Assert.False(reviewed.Setup.Transfers.Single().Relevant);
            Assert.Equal("unknown", reviewed.Setup.Transfers.Single().Allowance);
            Assert.Contains(reviewed.Findings, f => f.Check == "movement" && f.Result.Status == "unknown");
            Assert.Equal(new[] { window }, reviewed.Setup.Entries[museum].Context);
            Assert.True(reviewed.Setup.SingleTraveller);
        }
        var place = graph.PlacesById.Values.First();
        var places = graph.PlacesById.ToDictionary(); places[place.Id] = place with { Location = new(25, 35) };
        var allowanceEdit = saved.Setup with { Transfers = [saved.Setup.Transfers[0] with { AllowanceMinutes = 10 }] };
        var relocated = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", allowanceEdit, Now);
        Assert.False(relocated.Setup.Transfers.Single().Relevant);
        Assert.Equal(new[] { window }, relocated.Setup.Entries[lunch].Context);
        var unchanged = PlanningCheckService.Run(graph, "alice", allowanceEdit, Now);
        Assert.Empty(unchanged.Invalidated);
        Assert.True(unchanged.Setup.Transfers.Single().Relevant);
        Assert.Contains(unchanged.Findings, f => f.Check == "movement" && f.Result.Status == "completed" && f.Result.Minutes == 20);
    }

    /// <summary>Release and arrival Journeys bind their own routes and anchor locations even with an independent manual estimate.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Journey_route_dependencies_are_independent_of_estimate_source_and_recompute_only_changed_times(bool arrival)
    {
        var graph = Graph();
        var (_, museum, lunch, _) = Initial(graph);
        var origin = graph.PlacesById.Values.Single(p => p.Name == "Museum");
        var destination = graph.PlacesById.Values.Single(p => p.Name == "Lunch");
        var segmentId = Guid.NewGuid();
        var entry = (arrival ? destination : origin).Planning!.Entries[0] with { Kind = "travel" };
        var segment = new EditorSegmentDto(segmentId, graph.TripId, origin.Id, destination.Id, [], [], "rail", null, false,
            10, 30, "Manual", "", null, null, "", 0, new(true, true, true, true, true, false, false)) { Planning = new(1, [entry]) };
        graph = graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = segment } };
        var journey = $"segment/{segmentId}/{entry.Id}";
        var setup = new PlanningCheckSetup { Transfers = [new(arrival ? museum : journey, arrival ? journey : lunch,
            EstimateMinutes: 30, Relevant: true, Allowance: "additional", AllowanceMinutes: 15, Connection: "none")] };
        var saved = PlanningCheckService.Run(graph, "alice", setup, Now);
        Assert.Contains(saved.Findings, f => f.Check == "movement" && f.Result.Minutes == 15);
        foreach (var changed in new[] {
            segment with { FromPlaceId = destination.Id, ToPlaceId = origin.Id },
            segment with { Mode = "bus" }, segment with { TransportProfileId = Guid.NewGuid() },
            segment with { WaypointPlaceIds = [destination.Id], WaypointRouteVertexIndices = [1] },
            segment with { Route = JsonSerializer.SerializeToElement(new { type = "LineString", coordinates = new[] { new[] { 2, 2 }, new[] { 3, 3 } } }) } })
        {
            var reviewed = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = changed } }, "alice", saved.Setup, Now);
            Assert.False(reviewed.Setup.Transfers.Single().Relevant);
            Assert.Contains(reviewed.Invalidated, message => message.Contains("Movement"));
            Assert.DoesNotContain(reviewed.Findings, f => f.Check == "movement" && f.Result.Status == "completed");
        }
        // Change the anchor other than the selected Place; raw route geometry can stay absent or unchanged.
        var anchor = arrival ? destination : origin;
        var places = graph.PlacesById.ToDictionary(); places[anchor.Id] = anchor with { Location = new(25, 35) };
        var relocated = PlanningCheckService.Run(graph with { PlacesById = places }, "alice", saved.Setup, Now);
        Assert.False(relocated.Setup.Transfers.Single().Relevant);
        var renamed = segment with { NotesHtml = "Label context only", Planning = new(1, [entry with { Label = "Renamed" }]) };
        var labelOnly = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = renamed } }, "alice", saved.Setup, Now);
        Assert.Empty(labelOnly.Invalidated);
        var timing = entry with { When = arrival
            ? entry.When with { Start = new("known", new("dateTime", "2027-03-02T12:15", "UTC")) }
            : entry.When with { End = new("known", new("dateTime", "2027-03-02T11:15", "UTC")) } };
        var timeOnly = PlanningCheckService.Run(graph with { SegmentsById = new Dictionary<Guid, EditorSegmentDto> { [segmentId] = segment with { Planning = new(1, [timing]) } } }, "alice", saved.Setup, Now);
        Assert.Empty(timeOnly.Invalidated);
        Assert.True(timeOnly.Setup.Transfers.Single().Relevant);
        Assert.Contains(timeOnly.Findings, f => f.Check == "movement" && f.Result.Status == "completed" && f.Result.Minutes == (arrival ? 30 : 0));
    }

    /// <summary>Model display reordering preserves assessed participants and canonical physical pairs; material changes still reopen them.</summary>
    [Fact]
    public void Physical_premises_survive_display_order_but_not_membership_meaning_or_location_changes()
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "alice", Name = "Trip" };
        PlanningAggregate Presence() => new(1, [new(Guid.NewGuid(), new("period",
            Start: new("known", new("dateTime", "2027-03-02T10:00", "UTC")), End: new("known", new("dateTime", "2027-03-02T12:00", "UTC"))), Kind: "presence")]);
        var first = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, Name = "First", Center = new Point(0, 0), DisplayOrder = 0, Planning = Presence() };
        var second = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, Name = "Second", Center = new Point(1, 1), DisplayOrder = 1, Planning = Presence() };
        trip.Regions = [first, second];
        EditorTripStateDto Project() => EditorTripStateMapper.ToEditorState(trip, new Dictionary<Guid, IReadOnlyList<PlaceVisitEvent>>(), null!, null, null);
        var graph = Project();
        var keys = PlanningCheckService.Run(graph, "alice", new(), Now).Inventory.Select(i => i.Key).Order(StringComparer.Ordinal).ToArray();
        var pair = string.Join('|', keys);
        var saved = PlanningCheckService.Run(graph, "alice", new() { SingleTraveller = true,
            Incompatible = new Dictionary<string, string?> { [pair] = null } }, Now);
        Assert.Contains(saved.Findings, f => f.Check == "physical" && f.Result.Status == "contradiction");
        first.DisplayOrder = 1; second.DisplayOrder = 0;
        var reordered = PlanningCheckService.Run(Project(), "alice", saved.Setup, Now);
        Assert.Empty(reordered.Invalidated);
        Assert.True(reordered.Setup.SingleTraveller);
        Assert.Equal(saved.Setup.Incompatible[pair], reordered.Setup.Incompatible[pair]);
        Assert.Contains(reordered.Reused, r => r.Contains("Incompatible-location"));
        Assert.Contains(reordered.Findings, f => f.Check == "physical" && f.Result.Status == "contradiction");
        second.Center = new Point(2, 2);
        var relocated = PlanningCheckService.Run(Project(), "alice", saved.Setup, Now);
        Assert.Empty(relocated.Setup.Incompatible);
        Assert.True(relocated.Setup.SingleTraveller);
        second.Planning = new(1, [second.Planning!.Entries[0] with { Kind = "stay" }]);
        var meaning = PlanningCheckService.Run(Project(), "alice", saved.Setup, Now);
        Assert.False(meaning.Setup.SingleTraveller);
        Assert.Empty(meaning.Setup.Incompatible);
        var membership = PlanningCheckService.Run(graph, "alice", saved.Setup with { Selection = [keys[0]] }, Now);
        Assert.False(membership.Setup.SingleTraveller);
        Assert.Empty(membership.Setup.Incompatible);
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
