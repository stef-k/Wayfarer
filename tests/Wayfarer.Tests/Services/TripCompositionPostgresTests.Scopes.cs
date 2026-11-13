using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    [PostgresFact]
    public async Task RegionOnlyAndChildrenHonorPerChildReuseSkipNamingAndContainers()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var existing = c.Regions.Single(); var sourceRegion = b.Regions.Single();
        var sourcePlaces = sourceRegion.Places.ToArray(); var reuse = existing.Places.First();
        var regionOrder = new TripCompositionPlacement(c.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray(), "start");
        await Select(db, a.UserId, created, "b", [new("region", sourceRegion.Id, "copy", Name: "Only", Placement: regionOrder)]);
        var only = (await Load(db, c.Id)).Regions.Single(r => r.Name == "Only");
        Assert.Empty(only.Places); Assert.Empty(only.Areas); Assert.Null(only.Planning);
        var map = await Mapping(db, created, "b", "region", sourceRegion.Id);
        var children = sourcePlaces.Select((p, i) => i == 0
            ? new TripCompositionItem("place", p.Id, "reuse", DestinationId: reuse.Id, ExpectedParentId: reuse.RegionId)
            : i == 1 ? new TripCompositionItem("place", p.Id, "copy", Name: "Child")
            : new TripCompositionItem("place", p.Id, "skip"))
            .Concat(sourceRegion.Areas.Select(a => new TripCompositionItem("area", a.Id, "copy"))).ToArray();
        c = await Load(db, c.Id);
        await Select(db, a.UserId, created, "b", [new("region", sourceRegion.Id, "copy", Name: "With children", Separate: true,
            ExpectedDecision: new(map.DestinationId, map.Decision), Placement: new(c.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray()), Children: children)]);
        c = await Load(db, c.Id); var copied = c.Regions.Single(r => r.Name == "With children");
        Assert.Single(copied.Places); Assert.Equal("Child", copied.Places.Single().Name); Assert.Equal(2, copied.Areas.Count);
        Assert.Equal(existing.Id, c.Regions.SelectMany(r => r.Places).Single(p => p.Id == reuse.Id).RegionId);
        Assert.Single(c.Segments); // Region scope never imports routes.
        Assert.Null(copied.Places.Single().Planning);
    }

    [PostgresFact]
    public async Task LazyUnassignedAndExplicitNormalRegionStaySeparateFromPosition()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var bRegion = b.Regions.Single();
        var regionOriginals = c.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray();
        await Select(db, a.UserId, created, "b", [new("place", bRegion.Places.First().Id, "copy", Name: "Fallback",
            ExpectedRegions: regionOriginals, Placement: new([]))]);
        c = await Load(db, c.Id); var fallback = c.Regions.Single(r => r.Name == "Unassigned Places");
        Assert.Equal(0, fallback.DisplayOrder); Assert.Single(fallback.Places);
        var area = bRegion.Areas.First();
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [new("area", area.Id, "copy", RegionId: fallback.Id, Placement: new([]))]));
        c = await Load(db, c.Id);
        await Select(db, a.UserId, created, "b", [new("area", area.Id, "copy", NewRegionName: "Normal", Name: "Park",
            ExpectedRegions: c.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray(),
            RegionPlacement: new(c.Regions.Where(r => r.Name != "Unassigned Places").Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray()), Placement: new([]))]);
        c = await Load(db, c.Id); Assert.Single(c.Regions.Single(r => r.Name == "Normal").Areas);
        Assert.Empty(c.Regions.Single(r => r.Id == fallback.Id).Areas);
    }

    [PostgresFact]
    public async Task IncompatibleOrMovedAfterReferenceRefusesAllItemsAndDecisions()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var region = c.Regions.Single(); var places = region.Places.ToArray();
        var placement = new TripCompositionPlacement(places.Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray(), "after", places[0].Id);
        db.Regions.Add(new Region { Id = Guid.NewGuid(), UserId = a.UserId, TripId = c.Id, Name = "Moved" }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var other = await db.Regions.AsNoTracking().SingleAsync(r => r.TripId == c.Id && r.Name == "Moved");
        await db.Places.Where(p => p.Id == places[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, other.Id));
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [
            new("place", b.Regions.Single().Places.First().Id, "copy", RegionId: region.Id, Name: "Never", Placement: placement)]));
        Assert.False(await db.Places.AnyAsync(p => p.Region.TripId == c.Id && p.Name == "Never"));
        Assert.Equal(before.Mappings, (await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings);
        c = await Load(db, c.Id); region = c.Regions.Single(r => r.Id == region.Id);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [
            new("place", b.Regions.Single().Places.First().Id, "copy", RegionId: region.Id, Name: "Never", Placement: new(region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray(), "after", region.Id))]));
    }

    [PostgresFact]
    public async Task OwnBPlanningAdoptionIsExplicitPrivateAndRemapsEntries()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var region = c.Regions.Single(); var sourcePlace = b.Regions.Single().Places.First();
        await Select(db, a.UserId, created, "b", [new("place", sourcePlace.Id, "copy", RegionId: region.Id, Name: "No plan",
            Placement: new(region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray()))]);
        c = await Load(db, c.Id); var target = c.Regions.Single().Places.Single(p => p.Name == "No plan"); Assert.Null(target.Planning);
        var freshness = c.UpdatedAt;
        await Select(db, a.UserId, created, "b", [new("place", sourcePlace.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            ExpectedDecision: new(target.Id, "included"), Fields: new(Planning: new("useSource", E(null))))]);
        c = await Load(db, c.Id); var planning = c.Regions.Single().Places.Single(p => p.Id == target.Id).Planning!;
        Assert.Equal(freshness, c.UpdatedAt); Assert.Equal("secret-plan", planning.Entries.Single().Label);
        Assert.NotEqual(sourcePlace.Planning!.Entries.Single().Id, planning.Entries.Single().Id);
    }
}
