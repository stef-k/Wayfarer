using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    private readonly IDataProtectionProvider selectionProtection = new EphemeralDataProtectionProvider();

    /// <summary>Captured material survives source deletion; selected siblings form a contiguous ordered block with no B metadata/planning adoption.</summary>
    [PostgresFact]
    public async Task SelectionCopiesCapturedBlockAndCreditsAfterSourceDeletion()
    {
        var a = await Seed(); var b = await Seed();
        await using var db = fixture.CreateContext();
        var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var region = c.Regions.Single();
        var before = region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToArray();
        await db.Trips.Where(t => t.Id == b.Id).ExecuteDeleteAsync();
        var order = region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray();
        var placement = new TripCompositionPlacement(order, "after", before[1].Id);
        var areaPlacement = new TripCompositionPlacement(region.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray(), "start");
        var area = b.Regions.Single().Areas.First();
        await Select(db, a.UserId, created, "b", [
            new("place", b.Regions.Single().Places.First().Id, "copy", RegionId: region.Id, Name: "Beach", Placement: placement),
            new("place", b.Regions.Single().Places.Last().Id, "copy", RegionId: region.Id, Name: "Castle", Placement: placement),
            new("area", area.Id, "copy", RegionId: region.Id, Name: "Polygon", Placement: areaPlacement)]);
        c = await Load(db, c.Id); region = c.Regions.Single();
        Assert.Equal(new[] { before[0].Name, before[1].Name, "Beach", "Castle", before[2].Name, before[3].Name }, region.Places.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        Assert.Null(region.Places.Single(p => p.Name == "Castle").Location);
        Assert.Null(region.Places.Single(p => p.Name == "Beach").Planning);
        Assert.Equal(1, region.Areas.Single(p => p.Name == "Polygon").Geometry.NumInteriorRings);
        Assert.Contains(c.Credits, credit => credit.SourceTitle == b.Name && credit.Licence == "CC-BY-4.0");
        Assert.Contains(c.Credits, credit => credit.Notices == "Retain this notice");
        Assert.Equal(a.Notes, c.Notes); Assert.Equal(a.CoverImageUrl, c.CoverImageUrl); Assert.Equal(a.Tags.Single().Id, c.Tags.Single().Id);
        var state = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        Assert.DoesNotContain("planning", state.SnapshotB!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(JsonSerializer.Deserialize<TripCompositionMapping[]>(state.Mappings!, TripCompositionSnapshot.Json)!, m => m.Source == "b");
    }

    /// <summary>Reuse is a durable product decision, while only selected fresh fields may change and disjoint edits survive.</summary>
    [PostgresFact]
    public async Task SelectedNotesRefuseStaleWithoutLosingDisjointEditsOrDecisions()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext();
        var sourcePlace = b.Regions.Single().Places.First();
        await db.Places.Where(p => p.Id == sourcePlace.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "<p>Captured B</p>"));
        var created = await StartSelection(db, a, b);
        var target = (await Load(db, created.TripId)).Regions.Single().Places.First();
        await db.Places.Where(p => p.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MarkerColor, "independent"));
        await Select(db, a.UserId, created, "b", [new("place", sourcePlace.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            Fields: new(Notes: new("useSource", E(target.Notes))))]);
        var saved = await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id);
        Assert.Equal("<p>Captured B</p>", saved.Notes); Assert.Equal("independent", saved.MarkerColor);
        Assert.Equal(target.RegionId, saved.RegionId); Assert.Equal(target.DisplayOrder, saved.DisplayOrder);
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        var stale = new TripCompositionItem("place", sourcePlace.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            ExpectedDecision: new(target.Id, "reuse"), Fields: new(Notes: new("clear", E(target.Notes))));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [stale]));
        Assert.Equal(before.Mappings, (await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings);
        Assert.Equal("<p>Captured B</p>", (await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id)).Notes);
        await Select(db, a.UserId, created, "b", [stale with { Fields = new(Notes: new("clear", E(saved.Notes))) }]);
        Assert.Null((await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id)).Notes);
    }

    [PostgresTheory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task BothSourcesRemovalAndReviewedReaddPersistAcrossResume(string side)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var sourceArea = (side == "a" ? a : b).Regions.Single().Areas.First();
        var c = await Load(db, created.TripId); var region = c.Regions.Single();
        Guid destinationId; string decision;
        if (side == "b")
        {
            await Select(db, a.UserId, created, side, [new("area", sourceArea.Id, "copy", RegionId: region.Id,
                Separate: true, Placement: new(region.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(x => new TripCompositionOrderOriginal(x.Id, x.DisplayOrder)).ToArray()))]);
            var map = await Mapping(db, created, side, "area", sourceArea.Id); destinationId = map.DestinationId; decision = "separate";
        }
        else { var map = await Mapping(db, created, side, "area", sourceArea.Id); destinationId = map.DestinationId; decision = "included"; }
        var target = await db.Areas.AsNoTracking().SingleAsync(x => x.Id == destinationId);
        var removal = new TripCompositionItem("area", sourceArea.Id, "remove", DestinationId: destinationId,
            ExpectedDecision: new(destinationId, decision), Original: E(new { content = target.ToApiDto(), planning = target.Planning, notes = target.Notes, title = (string?)null }));
        await Select(db, a.UserId, created, side, [removal]);
        var resumed = await new TripCompositionService(db).ReadAsync(a.UserId, created.CompositionId!.Value);
        Assert.Contains("destinationRemoved", JsonSerializer.Serialize(resumed));
        Assert.Equal("destinationRemoved", (await Mapping(db, created, side, "area", sourceArea.Id)).Decision);
        var now = await Load(db, created.TripId);
        var readd = new TripCompositionItem("area", sourceArea.Id, "copy", RegionId: region.Id, Separate: true,
            ExpectedDecision: new(destinationId, "destinationRemoved"), Placement: new(now.Regions.Single().Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(x => new TripCompositionOrderOriginal(x.Id, x.DisplayOrder)).ToArray()));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, side, [readd]));
        await Select(db, a.UserId, created, side, [readd with { Readd = true }]);
        var restored = await Mapping(db, created, side, "area", sourceArea.Id);
        Assert.NotEqual(destinationId, restored.DestinationId); Assert.Equal("separate", restored.Decision);
    }

    /// <summary>Ordinary deletion is remembered durably on read and cannot be undone by reranking.</summary>
    [PostgresFact]
    public async Task ResumeMaterializesRemovedMappingWithoutPublicFreshness()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var area = c.Regions.Single().Areas.First();
        await db.Areas.Where(x => x.Id == area.Id).ExecuteDeleteAsync();
        var stamp = (await Load(db, c.Id)).UpdatedAt;
        await new TripCompositionService(db).ReadAsync(a.UserId, created.CompositionId!.Value);
        Assert.Equal(stamp, (await Load(db, c.Id)).UpdatedAt);
        var workspace = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        Assert.Contains("destinationRemoved", workspace.Mappings!);
        await new TripCompositionService(db).CandidatesAsync(a.UserId, workspace.Id, "a", "area", a.Regions.Single().Areas.First().Id, null);
        Assert.False(await db.Areas.AnyAsync(x => x.Id == area.Id));
    }

    [PostgresFact]
    public async Task AdditionalCopyRetainsInitialAMappingAndBothCurrentCounterparts()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var sourcePlace = a.Regions.Single().Places.First(); var initial = await Mapping(db, created, "a", "place", sourcePlace.Id);
        var c = await Load(db, created.TripId); var region = c.Regions.Single();
        await Select(db, a.UserId, created, "a", [new("place", sourcePlace.Id, "copy", RegionId: region.Id, Name: "Additional", Separate: true,
            ExpectedDecision: new(initial.DestinationId, initial.Decision), Placement: new(region.Places.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id)
                .Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray()))]);
        var workspace = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        var mappings = JsonSerializer.Deserialize<TripCompositionMapping[]>(workspace.Mappings!, TripCompositionSnapshot.Json)!
            .Where(m => m.Source == "a" && m.Kind == "place" && m.SourceId == sourcePlace.Id).ToArray();
        Assert.Equal(2, mappings.Length); Assert.Contains(mappings, m => m.DestinationId == initial.DestinationId && m.Decision == "included");
        Assert.Contains(mappings, m => m.DestinationId != initial.DestinationId && m.Decision == "separate");
        Assert.Equal(5, (await Load(db, c.Id)).Regions.Single().Places.Count);
    }

    private async Task<TripConstructionResult> StartSelection(ApplicationDbContext db, Trip a, Trip b)
    {
        var result = await new TripCompositionService(db).CreateAsync(a.UserId, a.Id, b.Id, null, null);
        fixture.RegisterTrip(result.TripId); return result;
    }
    private Task<object> Select(ApplicationDbContext db, string owner, TripConstructionResult result, string side, IReadOnlyList<TripCompositionItem> items,
        Func<CancellationToken, Task<bool>>? authority = null) => new TripCompositionService(db).ApplyAsync(owner, result.CompositionId!.Value,
            new(side, items), new(db, new(selectionProtection)), new(selectionProtection), new(selectionProtection, TimeProvider.System),
            new HashSet<string>(), new HashSet<string>(), authority);
    private static JsonElement E(object? value) => TripCompositionFieldValues.Element(value);
    private static async Task<TripCompositionMapping> Mapping(ApplicationDbContext db, TripConstructionResult result, string side, string kind, Guid sourceId)
    {
        var workspace = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == result.CompositionId);
        return JsonSerializer.Deserialize<TripCompositionMapping[]>(workspace.Mappings!, TripCompositionSnapshot.Json)!
            .Single(m => m.Source == side && m.Kind == kind && m.SourceId == sourceId);
    }
}
