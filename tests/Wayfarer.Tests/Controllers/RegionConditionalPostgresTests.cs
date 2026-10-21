using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real routed PostgreSQL evidence for the paired Region scalar boundary and retained legacy risks.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class RegionConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Disjoint originals survive in both directions and save orders, without structural or root replay.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DisjointEdits_BothPersist(bool webFirst, bool webNames)
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        async Task Save(bool isWeb)
        {
            var name = isWeb == webNames;
            var key = name ? "name" : isWeb ? "notesHtml" : "notes";
            using var response = await PatchAsync(isWeb ? web : bearer, region, isWeb,
                new Dictionary<string, object?> { [key] = name ? region.Name : region.Notes },
                new Dictionary<string, object?> { [key] = name ? "Edited name" : "Edited notes" });
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (isWeb) Assert.Equal(region.Id, result.GetProperty("affected").GetProperty("regions")[0].GetProperty("id").GetGuid());
            else Assert.Equal("bd2-1", result.GetProperty("contract").GetString());
        }
        await Save(webFirst);
        await Save(!webFirst);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Regions.AsNoTracking().SingleAsync(r => r.Id == region.Id);
        Assert.Equal(("Edited name", "Edited notes", region.CoverImageUrl, 1, 2d, 1d),
            (stored.Name, stored.Notes, stored.CoverImageUrl, stored.DisplayOrder, stored.Center!.X, stored.Center.Y));
        var trip = await verification.Trips.SingleAsync(t => t.Id == region.TripId);
        Assert.Equal(("Parent Trip", "Parent Notes", true), (trip.Name, trip.Notes, trip.IsPublic));
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"DisplayOrder\" =", sql));
        output.WriteLine($"Protected disjoint witness webFirst={webFirst}, webNames={webNames}: both persisted.");
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Complete Web functionality remains available without giving stale untouched fields write authority.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebCoverCenter_AndBearerNotes_Survive_ThenNotesOnlyCannotReplayView(bool webFirst)
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        async Task SaveWeb()
        {
            using var response = await PatchAsync(web, region, true,
                new { coverImage = new { rawUrl = region.CoverImageUrl }, center = new { latitude = 1, longitude = 2 } },
                new { coverImage = new { rawUrl = " https://example.test/new.jpg " }, center = new { latitude = 0, longitude = 0 } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        async Task SaveBearer()
        {
            using var response = await PatchAsync(bearer, region, false, new { notes = region.Notes }, new { notes = "Bearer notes" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (webFirst) { await SaveWeb(); await SaveBearer(); } else { await SaveBearer(); await SaveWeb(); }
        using var notesOnly = await PatchAsync(web, region, true, new { notesHtml = "Bearer notes" }, new { notesHtml = "Web notes" });
        Assert.Equal(HttpStatusCode.OK, notesOnly.StatusCode);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Regions.SingleAsync(r => r.Id == region.Id);
        Assert.Equal((region.Name, "Web notes", "https://example.test/new.jpg", 0d, 0d),
            (stored.Name, stored.Notes, stored.CoverImageUrl, stored.Center!.X, stored.Center.Y));
        using var clear = await PatchAsync(web, region, true,
            new { coverImage = new { rawUrl = stored.CoverImageUrl }, center = new { latitude = 0, longitude = 0 } },
            new { coverImage = (object?)null, center = (object?)null });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        stored = await verification.Regions.AsNoTracking().SingleAsync(r => r.Id == region.Id);
        Assert.Null(stored.Center);
        Assert.Null(stored.CoverImageUrl);
        Assert.Single(_warmup.Invocations); // Existing bearer policy only; Web Region saves schedule nothing.
    }

    /// <summary>Same-field conflicts are symmetric, reveal no current values and reject the entire dirty footprint.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SameField_RejectsAllChanges_ExplicitRefreshCanRetry(bool webFirst, bool name)
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        var key = name ? "name" : webFirst ? "notesHtml" : "notes";
        var otherKey = name ? (webFirst ? "notes" : "notesHtml") : "name";
        var original = name ? region.Name : region.Notes;
        using var first = await PatchAsync(webFirst ? web : bearer, region, webFirst,
            new Dictionary<string, object?> { [key] = original }, new Dictionary<string, object?> { [key] = "Private current value" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var secondKey = name ? "name" : webFirst ? "notes" : "notesHtml";
        await using var verification = fixture.CreateContext();
        var stamp = await verification.Trips.Where(t => t.Id == region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        var writes = probe.Updates.Count;
        var warmups = _warmup.Invocations.Count;
        using var stale = await PatchAsync(webFirst ? bearer : web, region, !webFirst,
            new Dictionary<string, object?> { [secondKey] = original, [otherKey] = name ? region.Notes : region.Name },
            new Dictionary<string, object?> { [secondKey] = "Stale edit", [otherKey] = "Must not commit" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadAsStringAsync();
        Assert.Contains("mutation-base-stale", problem);
        Assert.DoesNotContain("Private current value", problem);
        Assert.Equal(writes, probe.Updates.Count);
        Assert.Equal(warmups, _warmup.Invocations.Count);
        Assert.Equal(stamp, await verification.Trips.Where(t => t.Id == region.TripId).Select(t => t.UpdatedAt).SingleAsync());
        using var retry = await PatchAsync(webFirst ? bearer : web, region, !webFirst,
            new Dictionary<string, object?> { [secondKey] = "Private current value" }, new Dictionary<string, object?> { [secondKey] = name ? "Reviewed" : "" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var stored = await verification.Regions.SingleAsync(r => r.Id == region.Id);
        Assert.Equal(name ? "Reviewed" : "", name ? stored.Name : stored.Notes);
        Assert.Equal(name ? region.Notes : region.Name, name ? stored.Notes : stored.Name);
    }

    /// <summary>Display Notes never stand in for exact nullable originals; cover whitespace remains exact evidence.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<P>Legacy <STRONG>notes</STRONG></P>")]
    public async Task PublishedOriginals_NotesClearAndNullUnchanged(string? notes)
    {
        var region = await SeedAsync(notes);
        await using var db = fixture.CreateContext();
        await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CoverImageUrl, " https://example.test/exact.jpg "));
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        using var state = await web.GetAsync($"/api/trips/{region.TripId}/editor");
        Assert.True(state.StatusCode == HttpStatusCode.OK, await state.Content.ReadAsStringAsync());
        var dto = (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("regionsById").GetProperty(region.Id.ToString());
        var original = dto.GetProperty("originalValues");
        Assert.Equal(RichNotes.Normalize(notes), original.GetProperty("notesHtml").GetString());
        Assert.Equal(" https://example.test/exact.jpg ", original.GetProperty("coverImage").GetProperty("rawUrl").GetString());
        using var clear = await PatchAsync(web, region, true, new { notesHtml = RichNotes.Normalize(notes) }, new { notesHtml = "" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var nullUnchanged = await PatchAsync(bearer, region, false, new { name = region.Name, notes }, new { name = "Renamed", notes = (string?)null });
        Assert.Equal(HttpStatusCode.OK, nullUnchanged.StatusCode);
        using var staleNull = await PatchAsync(bearer, region, false, new { notes = (string?)null }, new { notes = "Do not apply" });
        Assert.Equal(HttpStatusCode.Conflict, staleNull.StatusCode);
        using var identity = await PatchAsync(bearer, region, false, new { notes = "" }, new { notes = "" });
        Assert.False((await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        await using var verification = fixture.CreateContext();
        Assert.Equal("", (await verification.Regions.FindAsync(region.Id))!.Notes);
    }

    /// <summary>Retains both pre-correction data-loss witnesses using existing routes, never a missing new endpoint.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyWriters_OverwriteOriginalDraftInBothDirections(bool webLast)
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        async Task SaveWeb() => Assert.Equal(HttpStatusCode.OK, (await web.PutAsJsonAsync(
            $"/api/trips/{region.TripId}/editor/regions/{region.Id}", new
            {
                name = "Web name", notesHtml = webLast ? region.Notes : "Web notes",
                coverImage = new { rawUrl = region.CoverImageUrl }, center = new { latitude = 1, longitude = 2 }
            })).StatusCode);
        async Task SaveBearer() => Assert.Equal(HttpStatusCode.OK, (await bearer.PutAsJsonAsync(
            $"/api/trips/regions/{region.Id}", new { name = webLast ? region.Name : "Stale bearer name", notes = "Bearer notes" })).StatusCode);
        if (webLast) { await SaveBearer(); await SaveWeb(); }
        else { await SaveWeb(); await SaveBearer(); }
        await using var verification = fixture.CreateContext();
        var stored = await verification.Regions.AsNoTracking().SingleAsync(r => r.Id == region.Id);
        Assert.Equal(webLast ? region.Notes : "Bearer notes", stored.Notes);
        Assert.Equal(webLast ? "Web name" : "Stale bearer name", stored.Name);
        output.WriteLine($"Legacy overwrite witness webLast={webLast}: persisted Name={stored.Name}; Notes={stored.Notes}");
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Registers scoped real database contexts and only replaces external warm-up dependencies.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor[]? interceptors = null) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors ?? []));
            services.AddSingleton(_warmup.Object);
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        });

    /// <summary>Authenticates a genuine User cookie with matching antiforgery evidence.</summary>
    private static async Task<HttpClient> WebAsync(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, userId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        return client;
    }

    /// <summary>Supplies only the actual incoming bearer credential.</summary>
    private static HttpClient Bearer(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", userId);
        return client;
    }

    /// <summary>Dispatches the new operation-specific contract through production routing.</summary>
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Region region, bool web, object expected, object changes) =>
        client.PatchAsJsonAsync(web ? $"/api/trips/{region.TripId}/editor/regions/{region.Id}/conditional"
            : $"/api/trips/{region.TripId}/conditional/regions/{region.Id}", new { expected, changes });

    /// <summary>Seeds an owned Region and incoming credential; fixture cleanup remains Trip/user scoped.</summary>
    private async Task<Region> SeedAsync(string? notes = "Original notes", string name = "Original Region")
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Parent Trip", Notes = "Parent Notes", IsPublic = true };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id,
            Name = name, Notes = notes, DisplayOrder = name == "Unassigned Places" ? 0 : 1,
            CoverImageUrl = "https://example.test/original.jpg", Center = new Point(2, 1) { SRID = 4326 } };
        await using var db = fixture.CreateContext();
        db.Regions.Add(region);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 4", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        return region;
    }
}
