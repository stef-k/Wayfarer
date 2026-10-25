using System.Net;
using System.Net.Http.Json;
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
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed PostgreSQL evidence for paired Area fields, precise Polygons and retained unconditional writers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class AreaConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Retained F1 protected-first counterexample now requires refusal with every stored field intact.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AreaRetainedWriterRejectsAfterProtectedNotes(bool webLast)
    {
        var area = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        using var first = await PatchAsync(webLast ? bearer : web, area, !webLast,
            webLast ? new { notes = area.Notes } : (object)new { notesHtml = area.Notes },
            webLast ? new { notes = "Protected committed Notes" } : (object)new { notesHtml = "Protected committed Notes" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using var before = fixture.CreateContext();
        var protectedRow = await before.Areas.AsNoTracking().SingleAsync(a => a.Id == area.Id);
        var protectedValues = before.Entry(protectedRow).CurrentValues.Clone();
        var protectedStamp = await before.Trips.AsNoTracking().Where(t => t.Id == area.Region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        using var second = webLast
            ? await web.PutAsJsonAsync($"/api/trips/{area.Region.TripId}/editor/areas/{area.Id}", new
            { name = "Unrelated Web name", notesHtml = area.Notes, fillHex = area.FillHex, geometry = Geometry(area) })
            : await bearer.PutAsJsonAsync($"/api/trips/areas/{area.Id}", new { notes = area.Notes });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var rejection = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-originals-required", rejection.GetProperty("code").GetString());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.AsNoTracking().SingleAsync(a => a.Id == area.Id);
        Assert.Equal("Protected committed Notes", stored.Notes);
        var storedValues = verify.Entry(stored).CurrentValues;
        foreach (var property in protectedValues.Properties)
            Assert.Equal(protectedValues[property], storedValues[property]);
        Assert.Equal(protectedStamp, await verify.Trips.AsNoTracking().Where(t => t.Id == area.Region.TripId).Select(t => t.UpdatedAt).SingleAsync());
    }

    /// <summary>Web Name/Fill/whole Polygon and bearer Notes survive either save order without replaying parent or order.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisjointEdits_BothSaveOrders_PreserveExactPolygonAndNotes(bool webFirst)
    {
        var area = await SeedAsync();
        var replacement = Polygon(20.123456789012344);
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        async Task Save(bool isWeb)
        {
            using var response = isWeb
                ? await PatchAsync(web, area, true, new { name = area.Name, fillHex = area.FillHex, geometry = Fingerprint(area) },
                    new { name = "Web Area", fillHex = " #ABCDEF ", geometry = Geometry(replacement) })
                : await PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "<p>Mobile notes</p>" });
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (isWeb) Assert.Equal(area.Id, result.GetProperty("affected").GetProperty("areas")[0].GetProperty("id").GetGuid());
            else Assert.Equal("bd2-1", result.GetProperty("contract").GetString());
        }
        await Save(webFirst);
        await Save(!webFirst);
        // A later Web Notes-only edit has no authority over the changed fill or Polygon.
        using var notesOnly = await PatchAsync(web, area, true, new { notesHtml = "<p>Mobile notes</p>" }, new { notesHtml = "Web notes" });
        Assert.Equal(HttpStatusCode.OK, notesOnly.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
        Assert.Equal(("Web Area", "Web notes", "#abcdef", area.RegionId, area.DisplayOrder),
            (stored.Name, stored.Notes, stored.FillHex, stored.RegionId, stored.DisplayOrder));
        AssertPolygon(replacement, stored.Geometry);
        var trip = await verify.Trips.SingleAsync(t => t.Id == area.Region.TripId);
        Assert.Equal(("Parent Trip", "Parent Notes", true), (trip.Name, trip.Notes, trip.IsPublic));
        Assert.Single(_warmup.Invocations);
        var lastArea = probe.Updates.Last(sql => sql.Contains("UPDATE \"Areas\""));
        Assert.Contains("\"Notes\" =", lastArea);
        Assert.DoesNotContain("\"Geometry\" =", lastArea);
        Assert.DoesNotContain("\"FillHex\" =", lastArea);
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"DisplayOrder\" =", sql));
        output.WriteLine($"BIDIRECTIONAL disjoint witness webFirst={webFirst}: exact 4326 Polygon/holes and both edits persisted.");
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Stale Notes reject the entire request in either direction, then explicit current-base Clear succeeds.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameNotes_ConflictIsUnapplied_ThenRefreshedBaseSucceeds(bool webFirst)
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        var firstKey = webFirst ? "notesHtml" : "notes";
        var secondKey = webFirst ? "notes" : "notesHtml";
        using var first = await PatchAsync(webFirst ? web : bearer, area, webFirst,
            new Dictionary<string, object?> { [firstKey] = area.Notes }, new Dictionary<string, object?> { [firstKey] = "Private newer notes" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var writes = probe.Updates.Count;
        var warmups = _warmup.Invocations.Count;
        var expected = new Dictionary<string, object?> { [secondKey] = area.Notes };
        var changes = new Dictionary<string, object?> { [secondKey] = "Stale notes" };
        if (!webFirst) { expected["name"] = area.Name; changes["name"] = "Must not commit"; }
        using var stale = await PatchAsync(webFirst ? bearer : web, area, !webFirst, expected, changes);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("mutation-base-stale", await stale.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Private newer notes", await stale.Content.ReadAsStringAsync());
        Assert.Equal(writes, probe.Updates.Count);
        Assert.Equal(warmups, _warmup.Invocations.Count);
        using var retry = await PatchAsync(webFirst ? bearer : web, area, !webFirst,
            new Dictionary<string, object?> { [secondKey] = "Private newer notes" }, new Dictionary<string, object?> { [secondKey] = "" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
        Assert.Equal((area.Name, ""), (stored.Name, stored.Notes));
        AssertPolygon(area.Geometry, stored.Geometry);
    }

    /// <summary>Lossy display null/defaults never replace originals, and explicit null Notes remains Unchanged.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<P>Legacy <STRONG>notes</STRONG></P>")]
    public async Task OriginalValues_PreserveNullableNotesAndFill(string? notes)
    {
        var area = await SeedAsync(notes, null);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        using var state = await web.GetAsync($"/api/trips/{area.Region.TripId}/editor");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        var dto = (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("areasById").GetProperty(area.Id.ToString());
        Assert.Equal("#ff6600", dto.GetProperty("fillHex").GetString());
        Assert.Equal(RichNotes.Normalize(notes), dto.GetProperty("originalValues").GetProperty("notesHtml").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("originalValues").GetProperty("fillHex").ValueKind);
        Assert.Equal(Fingerprint(area), dto.GetProperty("originalValues").GetProperty("geometry").GetString());
        using var falseOriginal = await PatchAsync(web, area, true, new { fillHex = "#ff6600" }, new { fillHex = "#123456" });
        Assert.Equal(HttpStatusCode.Conflict, falseOriginal.StatusCode);
        using var fill = await PatchAsync(web, area, true, new { fillHex = (string?)null, notesHtml = notes }, new { fillHex = "#123456", notesHtml = (string?)null });
        Assert.Equal(HttpStatusCode.OK, fill.StatusCode);
        using var clear = await PatchAsync(bearer, area, false, new { notes = RichNotes.Normalize(notes) }, new { notes = "" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var noOp = await PatchAsync(bearer, area, false, new { notes = "" }, new { notes = "" });
        Assert.False((await noOp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
        Assert.Equal(("", "#123456"), (stored.Notes, stored.FillHex));
        AssertPolygon(area.Geometry, stored.Geometry);
    }

    /// <summary>Area editing never rewrites sibling, Region, route, waypoint, measurement, provenance or visit rows.</summary>
    [PostgresFact]
    public async Task PopulatedGraph_OnlySelectedAreaAndTripStampChange()
    {
        var graph = await Wayfarer.Tests.Models.RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var area = await db.Areas.Include(a => a.Region).SingleAsync(a => a.Id == graph.AreaIds[0]);
        var future = DateTime.UtcNow.Date.AddDays(1);
        await db.Trips.Where(t => t.Id == graph.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, future));
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var snapshot = new NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 'region:' || r."Id"::text || ':' || r.xmin::text AS row FROM "Regions" r WHERE r."UserId" = $1
              UNION ALL SELECT 'area:' || a."Id"::text || ':' || a.xmin::text FROM "Areas" a
                JOIN "Regions" r ON r."Id" = a."RegionId" WHERE r."UserId" = $1 AND a."Id" <> $2
              UNION ALL SELECT 'place:' || p."Id"::text || ':' || p.xmin::text FROM "Places" p WHERE p."UserId" = $1
              UNION ALL SELECT 'segment:' || s."Id"::text || ':' || s.xmin::text FROM "Segments" s WHERE s."UserId" = $1
              UNION ALL SELECT 'waypoint:' || w."SegmentId"::text || ':' || w."Position"::text || ':' || w.xmin::text
                FROM "SegmentWaypoints" w JOIN "Segments" s ON s."Id" = w."SegmentId" WHERE s."UserId" = $1
              UNION ALL SELECT 'visit:' || v."Id"::text || ':' || v.xmin::text FROM "PlaceVisitEvents" v WHERE v."UserId" = $1
            ) dependents
            """, connection);
        snapshot.Parameters.AddWithValue(graph.UserId);
        snapshot.Parameters.AddWithValue(area.Id);
        var before = await snapshot.ExecuteScalarAsync();
        var probe = new AreaProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, graph.UserId);
        using var response = await PatchAsync(web, area, true, new { name = area.Name, notesHtml = area.Notes }, new { name = "Edited Area", notesHtml = "Changed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await snapshot.ExecuteScalarAsync());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
        Assert.Equal(area.DisplayOrder, stored.DisplayOrder);
        AssertPolygon(area.Geometry, stored.Geometry);
        Assert.Equal(future, (await verify.Trips.FindAsync(graph.TripId))!.UpdatedAt);
        Assert.Equal(2, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
        output.WriteLine("Populated dependent graph retained exact IDs/xmin; only selected Area fields and Trip UpdatedAt were written.");
    }

    /// <summary>Uses established routing/identity fixtures and fresh scoped database contexts; only external work is mocked.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor[]? interceptors = null) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors ?? []));
            services.AddSingleton(_warmup.Object);
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<IWebHostEnvironment>(env => env.WebRootPath == Directory.GetCurrentDirectory()));
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        });

    /// <summary>Obtains a real User-role cookie and matching antiforgery pair.</summary>
    private static async Task<HttpClient> WebAsync(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, userId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        return client;
    }

    /// <summary>Supplies just the incoming hashed API credential, distinct from cookie authority.</summary>
    private static HttpClient Bearer(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", userId);
        return client;
    }

    /// <summary>Adds the independently retained original Region identity to each routed field request.</summary>
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Area area, bool web, object expected, object changes)
    {
        var originals = JsonSerializer.SerializeToElement(expected).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        originals["regionId"] = area.RegionId;
        return client.PatchAsJsonAsync(web ? $"/api/trips/{area.Region.TripId}/editor/areas/{area.Id}/conditional"
            : $"/api/trips/{area.Region.TripId}/conditional/areas/{area.Id}", new { expected = originals, changes });
    }

    /// <summary>Seeds precise persisted Polygon holes and a credential, under the reusable fixture's scoped cleanup.</summary>
    private async Task<Area> SeedAsync(string? notes = "Original notes", string? fill = "#123456")
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Parent Trip", Notes = "Parent Notes", IsPublic = true };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "Original Region", DisplayOrder = 1 };
        var area = new Area { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "Original Area", Notes = notes,
            FillHex = fill, DisplayOrder = 7, Geometry = Polygon(10.123456789012344) };
        await using var db = fixture.CreateContext();
        db.Areas.Add(area);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 5", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        return area;
    }

    /// <summary>Material persisted geometry uses asymmetric lon/lat, full double precision and an interior hole.</summary>
    private static Polygon Polygon(double x) => new(new LinearRing([new(x, 1), new(x + 4, 1), new(x + 4, 5), new(x, 5), new(x, 1)]),
        [new LinearRing([new(x + 1, 2), new(x + 2, 2), new(x + 2, 3), new(x + 1, 3), new(x + 1, 2)])]) { SRID = 4326 };

    /// <summary>Uses published server evidence instead of constructing comparison JSON from display coordinates.</summary>
    private static string Fingerprint(Area area) => EditorTripStateMapper.ToArea(area.Region.TripId, area.RegionId, area).OriginalValues!.Geometry;

    /// <summary>Produces only the existing editable Polygon shape for desired replacements.</summary>
    private static JsonElement Geometry(Area area) => Geometry(area.Geometry);
    private static JsonElement Geometry(Polygon geometry) => JsonSerializer.SerializeToElement(new
    {
        type = "Polygon", coordinates = Enumerable.Range(0, geometry.NumInteriorRings + 1)
            .Select(i => (i == 0 ? geometry.ExteriorRing : geometry.GetInteriorRingN(i - 1)).Coordinates.Select(c => new[] { c.X, c.Y }))
    });

    /// <summary>Compares physical coordinates/ring sequence and SRID rather than merely topological shape.</summary>
    private static void AssertPolygon(Polygon expected, Polygon actual)
    {
        Assert.Equal(4326, actual.SRID);
        Assert.Equal(expected.NumInteriorRings, actual.NumInteriorRings);
        Assert.Equal(expected.AsBinary(), actual.AsBinary());
    }
}
