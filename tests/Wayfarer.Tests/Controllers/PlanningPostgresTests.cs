using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Wayfarer.Util;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real routed PostgreSQL planning authority, isolation, protected writes and writer preservation.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlanningPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    private readonly TestLogProvider logs = new();

    /// <summary>All five target types preserve disjoint content and reject stale/absent originals atomically.</summary>
    [PostgresFact]
    public async Task ProtectedWritesAndPrivatePublicSnapshots()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db);
        using var client = app.GetTestClient();
        var region = trip.Regions.Single();
        var place = region.Places.First();
        var area = region.Areas.Single();
        var segment = trip.Segments.Single();
        var before = await client.GetByteArrayAsync($"/api/trips/{trip.Id}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trip.UserId);
        var entries = Plan();
        foreach (var (kind, id, parent) in new[] { ("trip", trip.Id, trip.Id), ("region", region.Id, trip.Id),
            ("place", place.Id, region.Id), ("area", area.Id, region.Id), ("segment", segment.Id, trip.Id) })
        {
            var url = $"/api/trips/{trip.Id}/planning/{kind}/{id}";
            using var saved = await client.PatchAsJsonAsync(url, new { expected = new { parentId = parent, planning = (object?)null }, operation = "set", value = entries });
            Assert.True(saved.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Entries.Select(e => e.Exception?.ToString())));
            Assert.True(saved.Headers.CacheControl!.NoStore);
            Assert.True(saved.Headers.CacheControl.Private);
            using var stale = await client.PatchAsJsonAsync(url, new { expected = new { parentId = parent, planning = (object?)null }, operation = "clear" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var missing = await client.PatchAsJsonAsync(url, new { operation = "clear" });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
        using var owner = await client.GetAsync($"/api/trips/{trip.Id}/planning");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var snapshot = await owner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2027-03", snapshot.GetProperty("planning").GetProperty("trip").GetProperty("entries")[0].GetProperty("when").GetProperty("value").GetProperty("local").GetString());
        Assert.True(owner.Headers.CacheControl!.NoStore);
        Assert.Equal(before, await client.GetByteArrayAsync($"/api/trips/{trip.Id}"));
        var other = await fixture.CreateUserAsync();
        await using (var tokenDb = fixture.CreateContext())
        {
            tokenDb.ApiTokens.Add(new() { User = null!, UserId = other.Id, Name = "planning", TokenHash = ApiTokenService.HashToken(other.Id) });
            await tokenDb.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Id);
        using var forbiddenRead = await client.GetAsync($"/api/trips/{trip.Id}/planning");
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenRead.StatusCode);
        using var foreignWrite = await client.PatchAsJsonAsync($"/api/trips/{trip.Id}/planning/place/{place.Id}", new
        { expected = new { parentId = region.Id, planning = entries }, operation = "clear" });
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);
        Assert.DoesNotContain("2027", await foreignWrite.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(before, await client.GetByteArrayAsync($"/api/trips/{trip.Id}"));
        await using var verify = fixture.CreateContext();
        var persisted = await verify.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(JsonDocument.Parse(before).RootElement.GetProperty("updatedAt").GetDateTime(), persisted.UpdatedAt);
        Assert.False(JsonSerializer.SerializeToElement(persisted).TryGetProperty("Planning", out _));
        var clone = await new TripCloneCoordinator(verify).CloneAsync(trip.Id, other.Id);
        fixture.RegisterTrip(clone.ClonedTripId!.Value);
        verify.ChangeTracker.Clear();
        Assert.Null((await verify.Trips.SingleAsync(t => t.Id == clone.ClonedTripId)).Planning);
        Assert.All(await verify.Places.Where(p => p.Region.TripId == clone.ClonedTripId).ToArrayAsync(), p => Assert.Null(p.Planning));
    }

    /// <summary>Independent Notes, whole-period validation and explicit Clear use their real routed writers.</summary>
    [PostgresFact]
    public async Task InvalidReplacementRetainsSavedValuesAndNotesRemainIndependent()
    {
        var trip = await SeedAsync();
        var place = trip.Regions.Single().Places.First();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trip.UserId);
        var url = $"/api/trips/{trip.Id}/planning/place/{place.Id}";
        var plan = Plan();
        using var saved = await client.PatchAsJsonAsync(url, new { expected = new { parentId = place.RegionId, planning = (object?)null }, operation = "set", value = plan });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Entries.Select(e => e.Exception?.ToString())));
        using var notes = await client.PatchAsJsonAsync($"/api/trips/{trip.Id}/conditional/places/{place.Id}",
            new { expected = new { regionId = place.RegionId, notes = (string?)null }, changes = new { notes = "independent notes" } });
        Assert.True(notes.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Entries.Select(e => e.Exception?.ToString())));
        var invalid = new PlanningAggregate(1, [new(Guid.NewGuid(), new("period",
            Start: new("known", new("date", "2027-04-02")), End: new("known", new("date", "2027-03-02"))))]);
        using var rejected = await client.PatchAsJsonAsync(url, new { expected = new { parentId = place.RegionId, planning = plan }, operation = "set", value = invalid });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var verify = fixture.CreateContext();
        var actual = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.True(PlanningCodec.Equal(plan, actual.Planning));
        Assert.Equal("independent notes", actual.Notes);
        using var clear = await client.PatchAsJsonAsync(url, new { expected = new { parentId = place.RegionId, planning = plan }, operation = "clear" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id)).Planning);
    }

    /// <summary>Legacy delete/rebuild retains every same-ID/type aggregate; annotated removal rejects with full rollback.</summary>
    [PostgresFact]
    public async Task NativeReplacementPreservesPlanningAndRefusesUnreviewedLoss()
    {
        var trip = await SeedAsync();
        trip.Planning = Plan();
        trip.Regions.Single().Planning = Plan();
        foreach (var place in trip.Regions.Single().Places) place.Planning = Plan();
        trip.Regions.Single().Areas.Single().Planning = Plan();
        trip.Segments.Single().Planning = Plan();
        await using (var initial = fixture.CreateContext()) { initial.Update(trip); await initial.SaveChangesAsync(); }
        var kml = TripWayfarerKmlExporter.BuildKml(trip);
        Assert.DoesNotContain("name=\"Planning\"", kml);
        await using var db = fixture.CreateContext();
        var importer = new TripImportService(db, NullLogger<TripImportService>.Instance);
        await using (var file = new MemoryStream(Encoding.UTF8.GetBytes(kml)))
            await importer.ImportWayfarerKmlAsync(file, trip.UserId, TripImportMode.Upsert);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Trips.AsNoTracking().Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas).Include(t => t.Segments).SingleAsync(t => t.Id == trip.Id);
        Assert.True(PlanningCodec.Equal(trip.Planning, saved.Planning));
        Assert.True(PlanningCodec.Equal(trip.Regions.Single().Planning, saved.Regions.Single().Planning));
        Assert.All(saved.Regions.Single().Places, p => Assert.NotNull(p.Planning));
        Assert.NotNull(saved.Regions.Single().Areas.Single().Planning);
        Assert.NotNull(saved.Segments.Single().Planning);
        trip.Regions.Single().Areas.Clear();
        await using var removal = new MemoryStream(Encoding.UTF8.GetBytes(TripWayfarerKmlExporter.BuildKml(trip)));
        await Assert.ThrowsAsync<TripImportPlanningLossException>(() => importer.ImportWayfarerKmlAsync(removal, trip.UserId, TripImportMode.Upsert));
        Assert.Single(await verify.Areas.AsNoTracking().Where(a => a.Region.TripId == trip.Id).ToArrayAsync());
    }

    /// <summary>Cookie writes require antiforgery; fresh planning invalidates destructive consent even for kept children.</summary>
    [PostgresFact]
    public async Task CookiePlanningAndCurrentContentDeletionConsent()
    {
        var trip = await SeedAsync();
        var region = trip.Regions.Single();
        var place = region.Places.First();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db);
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, trip.UserId);
        var path = $"/api/trips/{trip.Id}/editor";
        var plan = Plan();
        var request = new { expected = new { parentId = region.Id, planning = (object?)null }, operation = "set", value = plan };
        using var csrf = await client.PatchAsJsonAsync($"{path}/planning/place/{place.Id}", request);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        var previewPath = $"{path}/regions/{region.Id}";
        using var preview = await client.PostAsJsonAsync(previewPath + "/deletion-preview", new { places = "keep", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var original = (await preview.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        using var saved = await client.PatchAsJsonAsync($"{path}/planning/place/{place.Id}", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var stale = await client.PostAsJsonAsync(previewPath + "/deletion", new { places = "keep", areas = "keep", confirmationToken = original.Confirmation.Token });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var freshPreview = await client.PostAsJsonAsync(previewPath + "/deletion-preview", new { places = "keep", areas = "keep" });
        var fresh = (await freshPreview.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        using var kept = await client.PostAsJsonAsync(previewPath + "/deletion", new { places = "keep", areas = "keep", confirmationToken = fresh.Confirmation.Token });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        await using var verify = fixture.CreateContext();
        var actual = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.NotEqual(region.Id, actual.RegionId);
        Assert.True(PlanningCodec.Equal(plan, actual.Planning));
        var area = await verify.Areas.AsNoTracking().SingleAsync(a => a.Id == region.Areas.Single().Id);
        using var areaPlan = await client.PatchAsJsonAsync($"{path}/planning/area/{area.Id}", new { expected = new { parentId = area.RegionId, planning = (object?)null }, operation = "set", value = plan });
        Assert.Equal(HttpStatusCode.OK, areaPlan.StatusCode);
        using var unreviewed = await client.DeleteAsync($"{path}/areas/{area.Id}");
        Assert.Equal(HttpStatusCode.Conflict, unreviewed.StatusCode);
        using var deletionRequest = new HttpRequestMessage(HttpMethod.Delete, $"{path}/areas/{area.Id}")
        { Content = JsonContent.Create(new { expected = new { parentId = area.RegionId, planning = plan }, operation = "clear" }) };
        using var reviewed = await client.SendAsync(deletionRequest);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        Assert.False(await verify.Areas.AsNoTracking().AnyAsync(a => a.Id == area.Id));
    }

    /// <summary>A failure after rebuilt children were inserted rolls back graph and retained private planning together.</summary>
    [PostgresFact]
    public async Task LateNativeReconciliationFailureRestoresOriginalPlanningAndGraph()
    {
        var trip = await SeedAsync();
        var place = trip.Regions.Single().Places.First();
        place.Planning = Plan();
        await using (var initial = fixture.CreateContext())
        {
            await initial.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Planning, place.Planning));
        }
        var beforeName = trip.Name;
        trip.Name = "Rejected replacement";
        var interceptor = new FailAfterRebuild();
        await using var db = fixture.CreateContext(interceptor);
        var importer = new TripImportService(db, NullLogger<TripImportService>.Instance);
        await using var file = new MemoryStream(Encoding.UTF8.GetBytes(TripWayfarerKmlExporter.BuildKml(trip)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportWayfarerKmlAsync(file, trip.UserId, TripImportMode.Upsert));
        Assert.Equal("Induced late reconciliation failure", failure.Message);
        Assert.True(interceptor.Rebuilt);
        await using var verify = fixture.CreateContext();
        Assert.Equal(beforeName, (await verify.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id)).Name);
        Assert.True(PlanningCodec.Equal(place.Planning, (await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id)).Planning));
        Assert.Equal(2, await verify.Places.CountAsync(p => p.Region.TripId == trip.Id));
    }

    /// <summary>Concurrent additions to one absent aggregate cannot both acknowledge success or merge implicitly.</summary>
    [PostgresFact]
    public async Task ConcurrentAbsentOriginalsHaveExactlyOneWinner()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trip.UserId);
        var first = Plan();
        var second = Plan();
        var url = $"/api/trips/{trip.Id}/planning/trip/{trip.Id}";
        var responses = await Task.WhenAll(client.PatchAsJsonAsync(url,
                new { expected = new { parentId = trip.Id, planning = (object?)null }, operation = "set", value = first }),
            client.PatchAsJsonAsync(url,
                new { expected = new { parentId = trip.Id, planning = (object?)null }, operation = "set", value = second }));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            await using var verify = fixture.CreateContext();
            var saved = (await verify.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id)).Planning;
            Assert.True(PlanningCodec.Equal(responses[0].IsSuccessStatusCode ? first : second, saved));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    /// <summary>Supplies real owned graph content with no planning before each routed test.</summary>
    private async Task<Trip> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Planning privacy", IsPublic = true };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "Region" };
        var first = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = user.Id, Name = "One", Location = new Point(23, 37) { SRID = 4326 } };
        var last = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = user.Id, Name = "Two", Location = new Point(24, 38) { SRID = 4326 } };
        region.Places.Add(first); region.Places.Add(last);
        region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Area", Geometry = new Polygon(new LinearRing([new(23, 37), new(24, 37), new(24, 38), new(23, 37)])) { SRID = 4326 } });
        trip.Regions.Add(region);
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, FromPlaceId = first.Id, ToPlaceId = last.Id,
            Mode = "", EstimatedDurationSource = EstimatedDurationSource.Manual, EstimatedDuration = TimeSpan.FromHours(1) });
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        db.ApiTokens.Add(new() { User = null!, UserId = user.Id, Name = "planning", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        fixture.RegisterTrip(trip.Id);
        return trip;
    }

    /// <summary>One rough unclassified month shared across target types.</summary>
    private static PlanningAggregate Plan() => new(1, [new(Guid.NewGuid(), new("at", new("month", "2027-03")))]);

    /// <summary>Uses production MVC/authentication/projection and the existing context factory seam.</summary>
    private Task<Microsoft.AspNetCore.Builder.WebApplication> HostAsync(ApplicationDbContext db) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs));
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        }, app => { app.Environment.WebRootPath = CreateTestDirectory(); });

    /// <summary>Fresh contexts keep owner/public snapshots independent of the request's mutation tracker.</summary>
    private sealed class Factory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    /// <summary>Injects one failure only after the production importer has actually rebuilt children.</summary>
    private sealed class FailAfterRebuild : DbCommandInterceptor
    {
        /// <summary>Confirms that the injected failure follows actual replacement inserts.</summary>
        public bool Rebuilt { get; private set; }
        /// <summary>Rejects the first measurement read after the rebuilt graph was sent to PostgreSQL.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Places\"", StringComparison.Ordinal)) Rebuilt = true;
            if (Rebuilt && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Segments\"", StringComparison.Ordinal)) throw new InvalidOperationException("Induced late reconciliation failure");
            return ValueTask.FromResult(result);
        }
    }
}
