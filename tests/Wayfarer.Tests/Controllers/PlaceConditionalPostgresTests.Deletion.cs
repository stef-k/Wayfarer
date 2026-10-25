using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>Both authorities require nonmutating consent, then atomically delete the approved footprint and retain history.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Deletion_BothAdaptersWithAndWithoutDependents(bool web, bool dependents)
    {
        var g = dependents ? await RegionDeletionTestGraph.SeedAsync(fixture) : null;
        await using var db = fixture.CreateContext();
        var place = g == null ? await SeedAsync() : await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == g.PlaceIds[0]);
        if (g != null) await AddCredentialAsync(db, place.UserId);
        var candidate = new PlaceVisitCandidate { Id = Guid.NewGuid(), UserId = place.UserId, PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow, LastHitUtc = DateTime.UtcNow, ConsecutiveHits = 1 };
        if (dependents) { db.PlaceVisitCandidates.Add(candidate); await db.SaveChangesAsync(); }
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, place.UserId) : Bearer(app, place.UserId);
        var path = DeletionPath(place, web);
        var before = await StoredStructureRowsAsync(place.Region.TripId);
        using var missing = await client.PostAsJsonAsync(path + "/deletion", new { });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var preview = await DeletionPreviewAsync(client, path);
        Assert.Equal(before, await StoredStructureRowsAsync(place.Region.TripId));
        Assert.Equal(dependents ? 1 : 0, preview.DeletedSegments.Count);
        Assert.Equal(dependents ? 2 : 0, preview.UpdatedSegments.Count);
        Assert.Equal(dependents ? 1 : 0, preview.UnlinkedVisits);
        Assert.Equal(dependents ? 1 : 0, preview.DeletedCandidates);
        // Disjoint Trip edits survive freshness stamping; future timestamps cannot be rewound.
        var future = DateTime.UtcNow.AddDays(1);
        await db.Trips.Where(t => t.Id == place.Region.TripId).ExecuteUpdateAsync(u => u.SetProperty(t => t.Name, "New Trip name").SetProperty(t => t.UpdatedAt, future));
        using var response = await client.PostAsJsonAsync(path + "/deletion", new { confirmationToken = preview.Confirmation.Token });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<EditorMutationResult<PlaceDeletionResultDto>>())!;
        Assert.Equal(new[] { place.Id }, result.DeletedIds.Places);
        Assert.Equal(preview.DeletedSegments.Ids, result.DeletedIds.Segments);
        Assert.Equal(preview.UpdatedSegments.Ids, result.Data.UpdatedSegmentIds);
        Assert.Equal(preview.PlaceOrderIds, result.Affected.PlaceOrdersByRegionId[place.RegionId]);
        Assert.Equal(preview.SegmentOrderIds, result.Affected.SegmentOrder);
        Assert.All(result.Affected.Segments, s => Assert.False(string.IsNullOrEmpty(s.AggregateConcurrencyToken)));
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Places.AnyAsync(p => p.Id == place.Id));
        Assert.False(await verify.PlaceVisitCandidates.AnyAsync(c => c.Id == candidate.Id));
        Assert.Equal("New Trip name", (await verify.Trips.FindAsync(place.Region.TripId))!.Name);
        Assert.True((await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt >= future.AddMilliseconds(-1));
        Assert.All(await verify.Places.Where(p => p.RegionId == place.RegionId).OrderBy(p => p.DisplayOrder).ToArrayAsync(), p => Assert.True(p.DisplayOrder > 0));
        if (g != null)
        {
            var visit = await verify.PlaceVisitEvents.SingleAsync(v => v.TripIdSnapshot == g.TripId);
            Assert.Null(visit.PlaceId); Assert.Equal("Visit notes", visit.NotesHtml); Assert.Equal(place.Name, visit.PlaceNameSnapshot);
            var remaining = await verify.Segments.Include(s => s.Waypoints).Where(s => s.TripId == g.TripId).OrderBy(s => s.DisplayOrder).ToArrayAsync();
            Assert.Equal(Enumerable.Range(1, remaining.Length), remaining.Select(s => s.DisplayOrder));
            Assert.DoesNotContain(remaining.SelectMany(s => s.Waypoints), w => w.PlaceId == place.Id);
            var custom = remaining.Single(s => s.Id == g.SurvivorIds[0]);
            Assert.Equal(7, custom.RouteGeometry!.NumPoints); Assert.Null(custom.RouteProvider); Assert.Null(custom.RouteInstructionsJson);
            Assert.Equal(EstimatedDurationSource.Manual, custom.EstimatedDurationSource);
            Assert.Equal(TimeSpan.FromTicks(123456780), custom.EstimatedDuration);
            Assert.NotEqual(123.25, custom.EstimatedDistanceKm);
            Assert.Equal(4, Assert.Single(custom.Waypoints).RouteVertexIndex);
            Assert.True(await verify.Places.AnyAsync(p => p.Id == g.PlaceIds[1]));
        }
        // Optional local evidence export feeds the mounted shipped validator with a genuine routed envelope.
        if (Environment.GetEnvironmentVariable("WAYFARER_DELETION_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"place-{web}-{dependents}.json"), JsonSerializer.Serialize(new { preview, result }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    /// <summary>Same-ID content, route/anchor/profile, sibling and membership changes invalidate the entire destructive consent.</summary>
    [PostgresTheory]
    [InlineData("target")]
    [InlineData("parent")]
    [InlineData("route")]
    [InlineData("anchor")]
    [InlineData("order")]
    [InlineData("dependency")]
    [InlineData("profile")]
    public async Task Deletion_MaterialDriftRejectsWithoutPartialMutation(string drift)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        await AddCredentialAsync(db, g.UserId);
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == g.PlaceIds[0]);
        await using var app = await StartAsync(db); using var client = Bearer(app, g.UserId);
        Guid? profileId = null;
        if (drift == "profile")
        {
            var profile = new TransportProfile { Id = Guid.NewGuid(), Key = "delete-" + Guid.NewGuid().ToString("N"), Label = "Deletion profile", Category = "ground", PlanningSpeedKmh = 5 };
            fixture.RegisterTransportProfile(profile.Id); db.Add(profile); await db.SaveChangesAsync(); profileId = profile.Id;
            await db.Segments.Where(s => s.Id == g.SurvivorIds[0]).ExecuteUpdateAsync(u => u.SetProperty(s => s.TransportProfileId, profile.Id).SetProperty(s => s.Mode, profile.Key));
        }
        var path = DeletionPath(place, false); var preview = await DeletionPreviewAsync(client, path);
        if (profileId.HasValue) await db.Set<TransportProfile>().Where(p => p.Id == profileId).ExecuteUpdateAsync(u => u.SetProperty(p => p.PlanningSpeedKmh, 8));
        if (drift == "target") await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Notes, "Changed same-ID content"));
        if (drift == "parent") await db.Regions.Where(r => r.Id == place.RegionId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Notes, "Changed parent"));
        if (drift == "route") await db.Segments.Where(s => s.Id == g.SurvivorIds[0]).ExecuteUpdateAsync(u => u.SetProperty(s => s.RouteAttribution, "Changed private provenance"));
        if (drift == "anchor") await db.Places.Where(p => p.Id == g.OutsidePlaceId).ExecuteUpdateAsync(u => u.SetProperty(p => p.RegionId, g.DestinationId!.Value));
        if (drift == "order") await db.Places.Where(p => p.Id == g.PlaceIds[1]).ExecuteUpdateAsync(u => u.SetProperty(p => p.DisplayOrder, 99));
        if (drift == "dependency") { db.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = g.TripId, UserId = g.UserId, FromPlaceId = place.Id, ToPlaceId = place.Id }); await db.SaveChangesAsync(); }
        var before = await GraphStateAsync(g.TripId); var rows = await StoredStructureRowsAsync(g.TripId);
        using var response = await client.PostAsJsonAsync(path + "/deletion", new { confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains("place-deletion-preview-stale", body);
        Assert.DoesNotContain("Changed private provenance", body);
        Assert.Equal(before, await GraphStateAsync(g.TripId)); Assert.Equal(rows, await StoredStructureRowsAsync(g.TripId));
    }

    /// <summary>Rollback failure and real committed acknowledgement loss remain server errors, never automatic deletion retries.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_RollbackOrCommittedLostResponse(bool committed)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext(); await AddCredentialAsync(db, g.UserId);
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == g.PlaceIds[0]);
        await using var app = await StartAsync(db, [new FailPlaceCommit(committed)]); using var web = await WebAsync(app, g.UserId);
        var path = DeletionPath(place, true); var preview = await DeletionPreviewAsync(web, path);
        var before = await GraphStateAsync(g.TripId); var rows = await StoredStructureRowsAsync(g.TripId);
        using var response = await web.PostAsJsonAsync(path + "/deletion", new { confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(!committed, await verify.Places.AnyAsync(p => p.Id == place.Id));
        if (!committed) { Assert.Equal(before, await GraphStateAsync(g.TripId)); Assert.Equal(rows, await StoredStructureRowsAsync(g.TripId)); }
        else Assert.Null((await verify.PlaceVisitEvents.SingleAsync(v => v.TripIdSnapshot == g.TripId)).PlaceId);
    }

    private static string DeletionPath(Place p, bool web) => $"/api/trips/{p.Region.TripId}/{(web ? "editor" : "conditional")}/places/{p.Id}";
    private static async Task<PlaceDeletionPreviewDto> DeletionPreviewAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsJsonAsync(path + "/deletion-preview", new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<PlaceDeletionPreviewDto>())!;
    }
}
