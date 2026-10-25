using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>Real Place waits permit independent commits; final rediscovery rejects newly added dependencies and expired or revoked consent.</summary>
    [PostgresTheory]
    [InlineData("dependency")]
    [InlineData("content")]
    [InlineData("expiry")]
    [InlineData("credential")]
    public async Task Deletion_RevalidatesAfterPhysicalPlaceWait(string drift)
    {
        var place = await SeedAsync(); var clock = new DeletionClock(); var probe = new PlaceProbe();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db, [probe], clock);
        using var client = Bearer(app, place.UserId); var path = DeletionPath(place, false);
        var preview = await DeletionPreviewAsync(client, path);
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        var pending = client.PostAsJsonAsync(path + "/deletion", new { confirmationToken = preview.Confirmation.Token });
        await WaitForLockAsync(probe, 0); Assert.False(pending.IsCompleted);
        // No later-class row is held by this fixture; inserting the new Segment cannot form an artificial cycle.
        if (drift == "dependency") { blocker.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = place.Region.TripId,
            UserId = place.UserId, FromPlaceId = place.Id, ToPlaceId = place.Id }); await blocker.SaveChangesAsync(); }
        if (drift == "content") await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Notes, "Edited while waiting"));
        if (drift == "expiry") clock.Now += TimeSpan.FromMinutes(11);
        if (drift == "credential") await blocker.ApiTokens.Where(t => t.UserId == place.UserId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        var before = await StoredStructureRowsAsync(place.Region.TripId);
        using var response = await pending;
        Assert.Equal(drift == "credential" ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await StoredStructureRowsAsync(place.Region.TripId));
        await using var verify = fixture.CreateContext(); Assert.True(await verify.Places.AnyAsync(p => p.Id == place.Id));
        Assert.Empty(probe.Updates);
    }

    /// <summary>Cookie, role and bearer scopes remain distinct; malformed and cross-Trip/owner requests reveal no footprint.</summary>
    [PostgresFact]
    public async Task Deletion_AuthorityStrictParsingAndClosedBypasses()
    {
        var place = await SeedAsync(); var other = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId); using var bearer = Bearer(app, place.UserId);
        var path = DeletionPath(place, false); var webPath = DeletionPath(place, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.PostAsJsonAsync(path + "/deletion-preview", new { })).StatusCode);
        using var noCsrf = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noCsrf, app.Services, place.UserId);
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync(webPath + "/deletion-preview", new { })).StatusCode);
        using var wrongRole = await WebAsync(app, place.UserId);
        wrongRole.DefaultRequestHeaders.Remove("Cookie");
        UserConnectionTokenHttpTests.Cookie(wrongRole, app.Services, place.UserId, "Admin");
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(wrongRole);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.PostAsJsonAsync(webPath + "/deletion-preview", new { })).StatusCode);
        foreach (var body in new[] { "{\"extra\":1}", "{\"confirmationToken\":\"a\",\"confirmationToken\":\"b\"}", "{\"confirmationToken\":null}", "[]" })
        {
            using var invalid = await bearer.PostAsync(path + "/deletion", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await bearer.PostAsJsonAsync(path + "/deletion", new { confirmationToken = "tampered" })).StatusCode);
        foreach (var client in new[] { bearer, web })
        {
            var prefix = ReferenceEquals(client, web) ? "editor" : "conditional";
            foreach (var target in new[] { $"/api/trips/{other.Region.TripId}/{prefix}/places/{other.Id}", $"/api/trips/{other.Region.TripId}/{prefix}/places/{place.Id}" })
            {
                using var foreign = await client.PostAsJsonAsync(target + "/deletion-preview", new { });
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
                Assert.DoesNotContain(place.Name, await foreign.Content.ReadAsStringAsync());
            }
            var deletePlace = ReferenceEquals(client, web) ? webPath : $"/api/trips/places/{place.Id}";
            var deleteRegion = ReferenceEquals(client, web) ? $"/api/trips/{place.Region.TripId}/editor/regions/{place.RegionId}" : $"/api/trips/regions/{place.RegionId}";
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(deletePlace)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(deleteRegion)).StatusCode);
        }
        await using var verify = fixture.CreateContext(); Assert.True(await verify.Places.AnyAsync(p => p.Id == place.Id));
    }

    /// <summary>The confirmation clock advances explicitly; no wall-clock sleeps are used to test expiry.</summary>
    private sealed class DeletionClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
