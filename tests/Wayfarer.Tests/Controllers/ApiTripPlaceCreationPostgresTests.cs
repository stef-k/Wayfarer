using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed paired creation proofs against the maintained PostgreSQL/PostGIS fixture.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiTripPlaceCreationPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Both transports create server identities, preserve sparse/null siblings, and publish the committed footprint.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairedCreation_PersistsExactFootprints(bool web)
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var client = await ClientAsync(app, region.UserId, web);
        var regions = await RegionOriginalsAsync(region.TripId);
        using var createRegion = await client.PostAsJsonAsync(RegionUrl(region, web), new { expected = new { regions }, value = RegionValue(web) });
        Assert.True(createRegion.IsSuccessStatusCode, await createRegion.Content.ReadAsStringAsync());
        var regionReply = await createRegion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bd2-1", regionReply.GetProperty("contract").GetString());
        var regionId = regionReply.GetProperty(web ? "data" : "region").GetProperty("id").GetGuid();
        Assert.DoesNotContain(regions, original => original.Id == regionId);
        var places = await PlaceOriginalsAsync(region.Id);
        var before = await FootprintAsync(region.UserId);
        var originalStamp = await db.Trips.AsNoTracking().Where(t => t.Id == region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        using var createPlace = await client.PostAsJsonAsync(PlaceUrl(region, web), new { expected = new { regionId = region.Id, places }, value = PlaceValue(web, region.Id) });
        Assert.True(createPlace.IsSuccessStatusCode, await createPlace.Content.ReadAsStringAsync());
        var placeReply = await createPlace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bd2-1", placeReply.GetProperty("contract").GetString());
        var placeId = placeReply.GetProperty(web ? "data" : "place").GetProperty("id").GetGuid();
        await using var fresh = fixture.CreateContext();
        var stored = await fresh.Places.SingleAsync(p => p.Id == placeId);
        Assert.Equal((region.Id, "Created Place", 5, "marker", "bg-blue", 0d, 0d),
            (stored.RegionId, stored.Name, stored.DisplayOrder, stored.IconName, stored.MarkerColor, stored.Location!.X, stored.Location.Y));
        Assert.Equal(web ? "Manual address" : null, stored.Address);
        Assert.Null(stored.AddressEnrichmentProvider);
        Assert.Null(stored.AddressEnrichedAt);
        Assert.Null(stored.ResolvedFeatureName);
        Assert.Equal(before, await FootprintAsync(region.UserId, placeId));
        var createdRegion = (await fresh.Regions.FindAsync(regionId))!;
        Assert.Equal(10, createdRegion.DisplayOrder);
        Assert.Equal(web ? "https://example.test/cover.jpg" : null, createdRegion.CoverImageUrl);
        Assert.Equal(web ? 0d : (double?)null, createdRegion.Center?.X);
        Assert.Equal(9, (await fresh.Regions.FindAsync(region.Id))!.DisplayOrder);
        Assert.Equal(new int?[] { null, 4, 4 }.Order(), (await fresh.Places.Where(p => p.RegionId == region.Id && p.Id != placeId).Select(p => p.DisplayOrder).ToArrayAsync()).Order());
        var trip = await fresh.Trips.FindAsync(region.TripId);
        Assert.Equal(("Parent", "Unrelated Trip Notes", true), (trip!.Name, trip.Notes, trip.IsPublic));
        Assert.True(trip.UpdatedAt >= originalStamp);
        Assert.Single(await fresh.Locations.Where(l => l.UserId == region.UserId).ToArrayAsync());
    }

    /// <summary>Membership/order/parent and canonical identity drift reject the whole operation; sibling content alone is not a dependency.</summary>
    [PostgresTheory]
    [InlineData("place-order")]
    [InlineData("place-membership")]
    [InlineData("parent")]
    [InlineData("region-order")]
    [InlineData("canonical")]
    public async Task StartingViewDrift_RejectsWithoutEffects(string drift)
    {
        var region = await SeedAsync();
        var regions = await RegionOriginalsAsync(region.TripId);
        var places = await PlaceOriginalsAsync(region.Id);
        await using var change = fixture.CreateContext();
        if (drift == "place-order") await change.Places.Where(p => p.Id == places[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayOrder, 22));
        if (drift == "place-membership")
        {
            change.Places.Add(new Place { Id = Guid.NewGuid(), UserId = region.UserId, RegionId = region.Id, Name = "Winner" });
            await change.SaveChangesAsync();
        }
        if (drift == "parent") await change.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, "foreign"));
        if (drift == "region-order") await change.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, 33));
        if (drift == "canonical") await change.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "Unassigned Places"));
        var before = await FootprintAsync(region.UserId);
        var stamp = await change.Trips.AsNoTracking().Where(t => t.Id == region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        await using var app = await StartAsync(change);
        using var client = await ClientAsync(app, region.UserId, false);
        var place = drift.StartsWith("place") || drift == "parent";
        using var response = place
            ? await client.PostAsJsonAsync(PlaceUrl(region, false), new { expected = new { regionId = region.Id, places }, value = PlaceValue(false, region.Id) })
            : await client.PostAsJsonAsync(RegionUrl(region, false), new { expected = new { regions }, value = RegionValue(false) });
        Assert.Equal(drift == "parent" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await FootprintAsync(region.UserId));
        await using var fresh = fixture.CreateContext();
        Assert.Equal(stamp, (await fresh.Trips.FindAsync(region.TripId))!.UpdatedAt);
    }

    /// <summary>Only the selected destination order matters; unrelated content and other destination edits survive.</summary>
    [PostgresFact]
    public async Task UntouchedSiblingContent_DoesNotInvalidateAppend()
    {
        var region = await SeedAsync();
        var places = await PlaceOriginalsAsync(region.Id);
        await using var db = fixture.CreateContext();
        await db.Places.Where(p => p.RegionId == region.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "New sibling content"));
        await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Notes, "New parent notes"));
        await using var app = await StartAsync(db);
        using var client = await ClientAsync(app, region.UserId, true);
        using var response = await client.PostAsJsonAsync(PlaceUrl(region, true), new { expected = new { regionId = region.Id, places }, value = PlaceValue(true, region.Id) });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await using var fresh = fixture.CreateContext();
        Assert.All(await fresh.Places.Where(p => places.Select(x => x.Id).Contains(p.Id)).ToArrayAsync(), p => Assert.Equal("New sibling content", p.Notes));
        Assert.Equal("New parent notes", (await fresh.Regions.FindAsync(region.Id))!.Notes);
    }

    /// <summary>Concurrent requests with one starting view admit only one append, including missing fallback creation.</summary>
    [PostgresTheory]
    [InlineData("place")]
    [InlineData("fallback")]
    [InlineData("region")]
    public async Task ConcurrentCreation_OneWinner_NoDuplicateOrGhost(string operation)
    {
        var fallback = operation == "fallback";
        var region = await SeedAsync();
        var probe = new TripLockProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe);
        using var first = await ClientAsync(app, region.UserId, false);
        using var second = await ClientAsync(app, region.UserId, false);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {region.TripId} FOR UPDATE");
        object expected = fallback ? new { regionId = (Guid?)null, places = Array.Empty<object>(), regions = await RegionOriginalsAsync(region.TripId) }
            : new { regionId = region.Id, places = await PlaceOriginalsAsync(region.Id) };
        if (operation == "region") expected = new { regions = await RegionOriginalsAsync(region.TripId) };
        var request = new { expected, value = operation == "region" ? RegionValue(false) : PlaceValue(false, fallback ? null : region.Id) };
        var url = operation == "region" ? RegionUrl(region, false) : PlaceUrl(region, false);
        var pending = first.PostAsJsonAsync(url, request);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForBlockAsync(probe.ProcessId);
        var competing = second.PostAsJsonAsync(url, request);
        await transaction.CommitAsync();
        using var a = await pending;
        using var b = await competing;
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }.Order(), new[] { a.StatusCode, b.StatusCode }.Order());
        await using var fresh = fixture.CreateContext();
        if (operation == "region") Assert.Single(await fresh.Regions.Where(r => r.TripId == region.TripId && r.Name == "Created Region").ToArrayAsync());
        else Assert.Single(await fresh.Places.Where(p => p.UserId == region.UserId && p.Name == "Created Place").ToArrayAsync());
        var fallbacks = await fresh.Regions.Where(r => r.TripId == region.TripId && r.Name == "Unassigned Places").ToArrayAsync();
        Assert.Equal(fallback ? 1 : 0, fallbacks.Length);
        if (fallback) Assert.Single(await fresh.Places.Where(p => p.RegionId == fallbacks[0].Id).ToArrayAsync());
    }

    /// <summary>Failure after all writes rolls back Place, staged fallback and freshness together.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailure_RollsBackWholeCreation(bool createRegion)
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        var stamp = (await db.Trips.FindAsync(region.TripId))!.UpdatedAt;
        await using var app = await StartAsync(db, new FailCommit());
        using var client = await ClientAsync(app, region.UserId, false);
        object expected = createRegion ? new { regions = await RegionOriginalsAsync(region.TripId) }
            : new { regionId = (Guid?)null, places = Array.Empty<object>(), regions = await RegionOriginalsAsync(region.TripId) };
        using var response = await client.PostAsJsonAsync(createRegion ? RegionUrl(region, false) : PlaceUrl(region, false),
            new { expected, value = createRegion ? RegionValue(false) : PlaceValue(false, null) });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var fresh = fixture.CreateContext();
        Assert.False(await fresh.Regions.AnyAsync(r => r.TripId == region.TripId && r.Name == "Unassigned Places"));
        Assert.False(await fresh.Places.AnyAsync(p => p.UserId == region.UserId && p.Name == "Created Place"));
        Assert.False(await fresh.Regions.AnyAsync(r => r.TripId == region.TripId && r.Name == "Created Region"));
        Assert.Equal(stamp, (await fresh.Trips.FindAsync(region.TripId))!.UpdatedAt);
    }

    /// <summary>Authority changes while queued cannot turn an earlier authorized preflight into a later commit.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedAuthorityDrift_DoesNotCreate(bool revokeToken)
    {
        var region = await SeedAsync();
        var probe = new TripLockProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe);
        using var client = await ClientAsync(app, region.UserId, false);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {region.TripId} FOR UPDATE");
        var pending = client.PostAsJsonAsync(RegionUrl(region, false),
            new { expected = new { regions = await RegionOriginalsAsync(region.TripId) }, value = RegionValue(false) });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForBlockAsync(probe.ProcessId);
        await using var authority = fixture.CreateContext();
        if (revokeToken) await authority.ApiTokens.Where(t => t.UserId == region.UserId).ExecuteDeleteAsync();
        else await authority.Users.Where(u => u.Id == region.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await transaction.CommitAsync();
        using var response = await pending;
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Conflict });
        await using var fresh = fixture.CreateContext();
        Assert.False(await fresh.Regions.AnyAsync(r => r.TripId == region.TripId && r.Name == "Created Region"));
    }

    /// <summary>Canonical identity is independent of its historical stored order; append does not repair it.</summary>
    [PostgresFact]
    public async Task ExistingCanonical_PreservesHistoricalOrderAndRequiresOwnPlaceOriginals()
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "Unassigned Places"));
        await using var app = await StartAsync(db);
        using var client = await ClientAsync(app, region.UserId, false);
        using var response = await client.PostAsJsonAsync(PlaceUrl(region, false), new
        {
            expected = new { regionId = region.Id, places = await PlaceOriginalsAsync(region.Id) }, value = PlaceValue(false, region.Id)
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var fresh = fixture.CreateContext();
        Assert.Equal(9, (await fresh.Regions.FindAsync(region.Id))!.DisplayOrder);
        Assert.Single(await fresh.Regions.Where(r => r.TripId == region.TripId && r.Name == "Unassigned Places").ToArrayAsync());
    }

    /// <summary>Strict contracts reject omissions/duplicates/server identities and participating unconditional routes cannot insert.</summary>
    [PostgresFact]
    public async Task InvalidAndBypassRequests_DoNotCreate()
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var bearer = await ClientAsync(app, region.UserId, false);
        using var web = await ClientAsync(app, region.UserId, true);
        var before = await FootprintAsync(region.UserId);
        foreach (var body in new[] { "{}", "{\"expected\":{},\"value\":{}}", "{\"expected\":{\"regions\":[],\"regions\":[]},\"value\":{}}",
            "{\"expected\":{\"regions\":[]},\"value\":{\"id\":\"client-id\",\"name\":\"No\",\"notes\":null}}" })
        {
            using var response = await bearer.PostAsync(RegionUrl(region, false), new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var partialPair = await bearer.PostAsJsonAsync(PlaceUrl(region, false), new
        {
            expected = new { regionId = region.Id, places = await PlaceOriginalsAsync(region.Id) },
            value = new { regionId = region.Id, name = "Invalid", notes = (string?)null, location = new { latitude = 0 }, iconName = "marker", markerColor = "bg-blue" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, partialPair.StatusCode);
        foreach (var path in new[] { $"/api/trips/{region.TripId}/regions", $"/api/trips/{region.TripId}/places" })
        {
            using var response = await bearer.PostAsJsonAsync(path, new { name = "Bypass" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var path in new[] { $"/api/trips/{region.TripId}/editor/regions", $"/api/trips/{region.TripId}/editor/regions/{region.Id}/places" })
        {
            using var response = await web.PostAsJsonAsync(path, new { name = "Bypass" });
            Assert.False(response.IsSuccessStatusCode);
        }
        Assert.Equal(before, await FootprintAsync(region.UserId));
    }

    /// <summary>Cookie-only callers cannot invoke bearer creation; current foreign and inactive scopes cannot create.</summary>
    [PostgresFact]
    public async Task AuthorityAndForeignScope_Reject()
    {
        var region = await SeedAsync();
        var other = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await ClientAsync(app, region.UserId, true);
        using var bearer = await ClientAsync(app, other.UserId, false);
        var request = new { expected = new { regions = await RegionOriginalsAsync(region.TripId) }, value = RegionValue(false) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.PostAsJsonAsync(RegionUrl(region, false), request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bearer.PostAsJsonAsync(RegionUrl(region, false), request)).StatusCode);
        await db.Users.Where(u => u.Id == other.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.PostAsJsonAsync(RegionUrl(other, false), request)).StatusCode);
    }

    /// <summary>Existing host/authentication seams mount production routing and preserve external service policy.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor? interceptor = null)
    {
        var webRoot = CreateTestDirectory();
        var markerDirectory = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(Path.Combine(markerDirectory, "marker.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css"), ".bg-blue{} .color-white{}");
        return IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptor == null ? [] : [interceptor]));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
            services.AddSingleton<IWebHostEnvironment>(Mock.Of<IWebHostEnvironment>(e => e.WebRootPath == webRoot));
            services.AddSingleton<IIconColorProvider, IconColorProvider>();
        });
    }

    /// <summary>Authenticates either the actual User cookie/antiforgery pair or only the incoming bearer.</summary>
    private static async Task<HttpClient> ClientAsync(WebApplication app, string userId, bool web)
    {
        var client = app.GetTestClient();
        if (web)
        {
            UserConnectionTokenHttpTests.Cookie(client, app.Services, userId);
            await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        }
        else client.DefaultRequestHeaders.Authorization = new("Bearer", userId);
        return client;
    }

    private static string RegionUrl(Region region, bool web) => $"/api/trips/{region.TripId}/{(web ? "editor/regions/conditional" : "conditional/regions")}";
    private static string PlaceUrl(Region region, bool web) => $"/api/trips/{region.TripId}/{(web ? $"editor/regions/{region.Id}/places/conditional" : "conditional/places")}";
    private static object RegionValue(bool web) => web ? new { name = "Created Region", notesHtml = "Notes", coverImage = new { rawUrl = "https://example.test/cover.jpg" }, center = new { latitude = 0, longitude = 0 } } : new { name = "Created Region", notes = "Notes" };
    private static object PlaceValue(bool web, Guid? regionId) => web
        ? new { name = "Created Place", notesHtml = "Notes", address = "Manual address", location = new { latitude = 0, longitude = 0 }, iconName = "marker", markerColor = "bg-blue", reverseGeocode = true }
        : new { regionId, name = "Created Place", notes = "Notes", location = new { latitude = 0, longitude = 0 }, iconName = "marker", markerColor = "bg-blue" };

    /// <summary>Originals are read before intervening writes and retained unchanged in the request.</summary>
    private async Task<RegionOriginal[]> RegionOriginalsAsync(Guid tripId)
    {
        await using var db = fixture.CreateContext();
        return await db.Regions.AsNoTracking().Where(r => r.TripId == tripId).Select(r => new RegionOriginal(r.Id, r.DisplayOrder, r.Name == "Unassigned Places")).ToArrayAsync();
    }
    private async Task<PlaceOriginal[]> PlaceOriginalsAsync(Guid regionId)
    {
        await using var db = fixture.CreateContext();
        return await db.Places.AsNoTracking().Where(p => p.RegionId == regionId).Select(p => new PlaceOriginal(p.Id, p.DisplayOrder)).ToArrayAsync();
    }

    /// <summary>Row versions detect incidental writes to existing children and Location history.</summary>
    private async Task<string?> FootprintAsync(string userId, Guid? excludedPlace = null)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 'place:' || "Id"::text || ':' || xmin::text AS row FROM "Places" WHERE "UserId" = $1 AND ($2::uuid IS NULL OR "Id" <> $2)
              UNION ALL SELECT 'segment:' || "Id"::text || ':' || xmin::text FROM "Segments" WHERE "UserId" = $1
              UNION ALL SELECT 'waypoint:' || w."SegmentId"::text || ':' || w."Position"::text || ':' || w.xmin::text
                FROM "SegmentWaypoints" w JOIN "Segments" s ON s."Id" = w."SegmentId" WHERE s."UserId" = $1
              UNION ALL SELECT 'location:' || "Id"::text || ':' || xmin::text FROM "Locations" WHERE "UserId" = $1
            ) children
            """, connection);
        query.Parameters.AddWithValue(userId);
        query.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)excludedPlace ?? DBNull.Value });
        return await query.ExecuteScalarAsync() as string;
    }

    /// <summary>Seeds sparse/tied/null order evidence and a route independent of new creation.</summary>
    private async Task<Region> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Parent", Notes = "Unrelated Trip Notes", IsPublic = true };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Source", DisplayOrder = 9 };
        foreach (var order in new int?[] { null, 4, 4 }) region.Places.Add(new Place { Id = Guid.NewGuid(), UserId = user.Id, Region = region, Name = "Sibling", DisplayOrder = order, Location = new Point(1, 1) { SRID = 4326 } });
        await using var db = fixture.CreateContext();
        db.Regions.Add(region);
        var siblings = region.Places.ToArray();
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, FromPlace = siblings[0], ToPlace = siblings[1],
            Mode = "walk", DisplayOrder = 8, RouteGeometry = new LineString([new(1, 1), new(2, 2), new(1, 1)]) { SRID = 4326 },
            RouteProvider = "retained", RouteAttribution = "Existing attribution", EstimatedDistanceKm = 12.5, EstimatedDuration = TimeSpan.FromMinutes(17), EstimatedDurationSource = EstimatedDurationSource.Manual };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, Place = siblings[2], Position = 0, RouteVertexIndex = 1 });
        db.Segments.Add(segment);
        db.Locations.Add(new Wayfarer.Models.Location { UserId = user.Id, TimeZoneId = "UTC", Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified), Coordinates = new Point(2, 3) { SRID = 4326 }, Address = "History address" });
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 creation", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        return region;
    }

    /// <summary>Injects failure after insert and freshness writes, before any commit acknowledgement.</summary>
    private sealed class FailCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data, InterceptionResult result, CancellationToken token = default) =>
            throw new InvalidOperationException("Controlled creation commit failure");
    }
    /// <summary>Observes the final parent lock so concurrency evidence includes a real PostgreSQL wait.</summary>
    private sealed class TripLockProbe : DbCommandInterceptor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ProcessId { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("\"Trips\"") && command.CommandText.Contains("FOR UPDATE"))
            {
                ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                Started.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
    private async Task WaitForBlockAsync(int processId)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids($1)) > 0", connection);
        query.Parameters.AddWithValue(processId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((bool)(await query.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("Creation did not reach a PostgreSQL row wait.");
    }
    private sealed record RegionOriginal(Guid Id, int DisplayOrder, bool IsCanonical);
    private sealed record PlaceOriginal(Guid Id, int? DisplayOrder);
}
