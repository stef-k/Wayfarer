using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>Mixed selected content, nested/shared route dependencies, fresh identities and credits commit into the same populated Trip.</summary>
    [PostgresFact]
    public async Task CopyIntoPopulatedTripPreservesIdentitySourceAndUnselectedContent()
    {
        var destination = await Seed(); var source = await Seed();
        await using var db = fixture.CreateContext();
        var before = JsonSerializer.Serialize(TripCompositionSnapshot.Project(await Load(db, source.Id), true));
        var review = await CopyReview(db, destination.UserId, source.Id, destination.Id);
        var region = source.Regions.Single(); var route = source.Segments.Single();
        var existing = destination.Regions.Single();
        var regionOrder = destination.Regions.Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray();
        var children = region.Places.Select(p => new TripCompositionItem("place", p.Id, "copy", Separate: true))
            .Concat(region.Areas.Select(a => new TripCompositionItem("area", a.Id, "copy", Separate: true))).ToArray();
        TripCompositionItem[] items = [new("region", region.Id, "copy", Name: "Copied region", Children: children,
                Placement: new(regionOrder, "start")),
            new("segment", route.Id, "copy", Separate: true, Anchors: region.Places.Where(p => p.Location != null)
                .Select(p => new TripCompositionAnchor(p.Id, null, null, null)).ToArray(),
                Placement: new(destination.Segments.Select(s => new TripCompositionOrderOriginal(s.Id, s.DisplayOrder)).ToArray(), "after", destination.Segments.Single().Id))];
        var result = Assert.IsType<TripCompositionApplied>(await Copy(db, destination.UserId, review with { Items = items }));
        Assert.True(result.PublicChanged); Assert.Equal(destination.Id, result.TripId); Assert.Equal(Guid.Empty, result.CompositionId);
        var saved = await Load(db, destination.Id);
        Assert.Equal(destination.Name, saved.Name); Assert.Equal(destination.IsPublic, saved.IsPublic);
        Assert.Equal(destination.ContributionPolicy, saved.ContributionPolicy); Assert.Equal(destination.CoverImageUrl, saved.CoverImageUrl);
        Assert.Equal(destination.Tags.Single().Id, saved.Tags.Single().Id); Assert.True(Wayfarer.Models.Planning.PlanningCodec.Equal(destination.Planning, saved.Planning));
        var untouched = saved.Regions.Single(r => r.Id == existing.Id);
        Assert.Equal(existing.Places.Select(p => p.Id).Order(), untouched.Places.Select(p => p.Id).Order());
        var added = saved.Regions.Single(r => r.Name == "Copied region");
        Assert.Equal(1, added.DisplayOrder); Assert.Equal(2, untouched.DisplayOrder);
        Assert.Equal(region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Name),
            added.Places.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        Assert.All(added.Places, p => { Assert.DoesNotContain(region.Places, old => old.Id == p.Id); Assert.Null(p.Planning); });
        Assert.All(added.Areas, a => Assert.Equal(1, a.Geometry.NumInteriorRings));
        var copiedRoute = saved.Segments.Single(s => s.Id != destination.Segments.Single().Id);
        Assert.Equal(route.EstimatedDuration!.Value.Ticks, copiedRoute.EstimatedDuration!.Value.Ticks);
        Assert.Equal(route.EstimatedDistanceKm, copiedRoute.EstimatedDistanceKm); Assert.Equal(route.Title, copiedRoute.Title);
        Assert.Equal(route.RouteGeometry!.AsText(), copiedRoute.RouteGeometry!.AsText());
        Assert.Contains(added.Places, p => p.Id == copiedRoute.FromPlaceId);
        Assert.Contains(added.Places, p => p.Id == copiedRoute.ToPlaceId);
        Assert.Equal(added.Places.Single(p => p.Name == "Via").Id, copiedRoute.Waypoints.Single().PlaceId);
        Assert.Equal(1, copiedRoute.Waypoints.Single().RouteVertexIndex); Assert.Equal(2, copiedRoute.DisplayOrder);
        Assert.Contains(saved.Credits, c => c.SourceTitle == source.Name && c.Licence == "CC-BY-4.0");
        Assert.False(await db.Set<TripComposition>().AnyAsync(w => w.TripId == destination.Id));
        Assert.False(await db.Set<TripOrigin>().AnyAsync(o => o.TripId == destination.Id));
        Assert.Equal(before, JsonSerializer.Serialize(TripCompositionSnapshot.Project(await Load(db, source.Id), true)));
    }

    /// <summary>Selected source changes, destination order drift and late route incompatibility each refuse the entire operation.</summary>
    [PostgresTheory]
    [InlineData("source")]
    [InlineData("order")]
    [InlineData("route")]
    public async Task CopyRefusesStaleOrIncompatibleChoicesWithoutPartialContent(string change)
    {
        var destination = await Seed(); var source = await Seed();
        await using var db = fixture.CreateContext();
        var review = await CopyReview(db, destination.UserId, source.Id, destination.Id);
        var parent = destination.Regions.Single(); var place = source.Regions.Single().Places.First();
        var placement = new TripCompositionPlacement(parent.Places.Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray());
        var items = new List<TripCompositionItem> { new("place", place.Id, "copy", Name: "Must roll back", RegionId: parent.Id, Placement: placement) };
        if (change == "source") await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "<p>New source</p>"));
        if (change == "order") await db.Places.Where(p => p.Id == parent.Places.First().Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayOrder, 80));
        if (change == "route") items.Add(new("segment", source.Segments.Single().Id, "copy", Anchors: source.Regions.Single().Places.Where(p => p.Location != null)
            .Select(p => new TripCompositionAnchor(p.Id, parent.Places.First().Id, parent.Id, [12, 40])).ToArray(), Placement: new(destination.Segments.Select(s => new TripCompositionOrderOriginal(s.Id, s.DisplayOrder)).ToArray())));
        var before = JsonSerializer.Serialize(TripCompositionSnapshot.Project(await Load(db, destination.Id), true));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Copy(db, destination.UserId, review with { Items = items }));
        Assert.Equal(before, JsonSerializer.Serialize(TripCompositionSnapshot.Project(await Load(db, destination.Id), true)));
    }

    /// <summary>Only selected source/destination fields govern adoption; own planning is explicit and private-only saves retain public freshness.</summary>
    [PostgresFact]
    public async Task CopyExplicitAdoptionPreservesDisjointFieldsAndPrivateFreshness()
    {
        var destination = await Seed(); var source = await Seed(destination.UserId);
        await using var db = fixture.CreateContext();
        var target = destination.Regions.Single().Places.First(); var original = source.Regions.Single().Places.First();
        var review = await CopyReview(db, destination.UserId, source.Id, destination.Id);
        await db.Places.Where(p => p.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MarkerColor, "independent"));
        var stamp = (await Load(db, destination.Id)).UpdatedAt;
        var item = new TripCompositionItem("place", original.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            Fields: new(Planning: new("useSource", E(target.Planning))));
        var response = Assert.IsType<TripCompositionApplied>(await Copy(db, destination.UserId, review with { Items = [item] }));
        Assert.False(response.PublicChanged); Assert.Equal(stamp, (await Load(db, destination.Id)).UpdatedAt);
        var saved = await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id);
        Assert.Equal("independent", saved.MarkerColor); Assert.Equal(target.DisplayOrder, saved.DisplayOrder);
        Assert.NotEqual(original.Planning!.Entries[0].Id, saved.Planning!.Entries[0].Id);
        Assert.Equal(original.Planning.Entries[0].Label, saved.Planning.Entries[0].Label);
    }

    /// <summary>Foreign private/unlicensed/view-only/inactive sources cannot be previewed or saved; foreign private planning/media is absent.</summary>
    [PostgresTheory]
    [InlineData("private")]
    [InlineData("unlicensed")]
    [InlineData("view-only")]
    [InlineData("inactive")]
    public async Task CopyRechecksCurrentEligibility(string revoked)
    {
        var destination = await Seed(); var source = await Seed();
        await using var db = fixture.CreateContext();
        var projection = await new TripCompositionService(db).PreviewCopyAsync(destination.UserId, source.Id, destination.Id);
        var json = JsonSerializer.SerializeToElement(projection, TripCompositionSnapshot.Json).GetProperty("source").ToString();
        Assert.DoesNotContain("secret-plan", json); Assert.DoesNotContain("private-image", json);
        var root = JsonSerializer.SerializeToElement(projection);
        Assert.False(root.GetProperty("source").TryGetProperty("planning", out _));
        db.ChangeTracker.Clear();
        var review = await CopyReview(db, destination.UserId, source.Id, destination.Id);
        if (revoked == "private") await db.Trips.Where(t => t.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsPublic, false));
        if (revoked == "unlicensed") await db.Trips.Where(t => t.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, (string?)null));
        if (revoked == "view-only") await db.Trips.Where(t => t.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, "view-only"));
        if (revoked == "inactive") await db.Users.Where(u => u.Id == source.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var item = new TripCompositionItem("region", source.Regions.Single().Id, "copy", Name: "Refused", Placement: new(destination.Regions.Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray()));
        var error = await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Copy(db, destination.UserId, review with { Items = [item] }));
        Assert.Equal(404, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(error.ToActionResult()).StatusCode);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => new TripCompositionService(db).PreviewCopyAsync(destination.UserId, source.Id, destination.Id));
        Assert.Single((await Load(db, destination.Id)).Regions);
    }

    /// <summary>Actual lock contention cannot renew reviewed source content or selected destination originals.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyRechecksSelectedOriginalsAfterActualRowWait(bool sourceChanges)
    {
        var destination = await Seed(); var source = await Seed(destination.UserId);
        await using var writer = fixture.CreateContext();
        var review = await CopyReview(writer, destination.UserId, source.Id, destination.Id);
        var original = source.Regions.Single().Places.First(); var target = destination.Regions.Single().Places.First();
        var heldId = sourceChanges ? original.Id : target.Id;
        await using var held = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {heldId} FOR UPDATE");
        await using var applying = fixture.CreateContext(); await applying.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)applying.Database.GetDbConnection()).ProcessID;
        var item = new TripCompositionItem("place", original.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            Fields: new(Notes: new("useSource", E(target.Notes))));
        var task = Copy(applying, destination.UserId, review with { Items = [item] });
        try
        {
            await AssertBlocked(writer, pid);
            await writer.Places.Where(p => p.Id == heldId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "<p>Winner</p>"));
        }
        finally { await held.CommitAsync(); }
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => task);
        var unchanged = await Load(writer, destination.Id);
        Assert.Equal(sourceChanges ? target.Notes : "<p>Winner</p>", unchanged.Regions.Single().Places.Single(p => p.Id == target.Id).Notes);
        Assert.Equal(destination.Credits, unchanged.Credits);
    }

    private static async Task<TripCopyRequest> CopyReview(ApplicationDbContext db, string owner, Guid source, Guid destination)
    {
        var preview = JsonSerializer.SerializeToElement(await new TripCompositionService(db).PreviewCopyAsync(owner, source, destination), TripCompositionSnapshot.Json);
        db.ChangeTracker.Clear();
        return new(source, destination, preview.GetProperty("context").GetString()!,
            preview.GetProperty("originals").Deserialize<TripCopyOriginal[]>(TripCompositionSnapshot.Json)!, []);
    }
    private Task<object> Copy(ApplicationDbContext db, string owner, TripCopyRequest request, Func<CancellationToken, Task<bool>>? authority = null) =>
        new TripCompositionService(db).CopyAsync(owner, request, new(db, new(selectionProtection)), new(selectionProtection),
            new(selectionProtection, TimeProvider.System), new HashSet<string>(), new HashSet<string>(), authority);
}
