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
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Services;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed PostgreSQL contracts for paired Segment Notes and the retained complete aggregate writer.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class SegmentConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Negative control: the existing bearer PUT has no original compare and can restore stale Notes.</summary>
    [PostgresFact]
    public async Task LegacyBearerNotes_OverwritesInterveningCompleteWebNotes()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        var dto = await PublishedAsync(app, seed);
        using var updated = await web.PutAsJsonAsync(WebUrl(seed), FullRequest(dto, "Web committed notes"));
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        Assert.Equal("Web committed notes", (await ReadAsync(verify, seed)).Notes);
        using var legacy = await bearer.PutAsJsonAsync($"/api/trips/segments/{seed.SegmentId}", new { notes = dto.NotesHtml });
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(dto.NotesHtml, (await ReadAsync(verify, seed)).Notes);
        output.WriteLine("RED CONTROL: real legacy bearer PUT overwrote an intervening complete Web Notes save; no original compare.");
    }

    /// <summary>Either protected adapter rejects a stale same-field base with zero SQL writes, then accepts an explicitly adopted base.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairedNotes_StaleRejectsAndReviewedRetrySucceeds(bool webFirst)
    {
        var seed = await SeedAsync();
        var probe = new SegmentProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        var dto = await PublishedAsync(app, seed);
        using var first = await PatchAsync(webFirst ? web : bearer, seed, webFirst, dto.NotesHtml, "First notes");
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Assert.Equal(2, probe.Updates.Count);
        var before = await SnapshotAsync(seed);
        using var stale = await PatchAsync(webFirst ? bearer : web, seed, !webFirst, dto.NotesHtml, "Stale notes");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("mutation-base-stale", await stale.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(seed));
        Assert.Equal(2, probe.Updates.Count);
        using var retry = await PatchAsync(webFirst ? bearer : web, seed, !webFirst, "First notes", "Reviewed notes");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal("Reviewed notes", (await ReadAsync(verify, seed)).Notes);
        Assert.Empty(_warmup.Invocations);
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Independent Segment Notes survive in either adapter order without replacing sibling order or root scalars.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisjointSegmentNotes_PreserveBoth(bool webFirst)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var second = new Segment { Id = Guid.NewGuid(), TripId = seed.TripId, UserId = seed.UserId, Notes = null, DisplayOrder = 2 };
        db.Segments.Add(second);
        await db.SaveChangesAsync();
        var other = seed with { SegmentId = second.Id };
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        async Task Save(bool isWeb)
        {
            using var response = await PatchAsync(isWeb ? web : bearer, isWeb ? seed : other, isWeb,
                isWeb ? "original notes" : null, isWeb ? "Web notes" : "Mobile notes");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await Save(webFirst);
        await Save(!webFirst);
        await using var verify = fixture.CreateContext();
        Assert.Equal("Web notes", (await ReadAsync(verify, seed)).Notes);
        Assert.Equal("Mobile notes", (await verify.Segments.FindAsync(second.Id))!.Notes);
        Assert.Equal(2, (await verify.Segments.FindAsync(second.Id))!.DisplayOrder);
    }

    /// <summary>Uses the existing scoped Identity host and genuine tokens/cookies; external work stays mocked.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, params IInterceptor[] interceptors) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors));
            services.AddSingleton(_warmup.Object);
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<IWebHostEnvironment>(env => env.WebRootPath == Directory.GetCurrentDirectory()));
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        });

    /// <summary>Installs the real User-role cookie and matching antiforgery pair.</summary>
    private static async Task<HttpClient> WebAsync(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, userId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        return client;
    }

    /// <summary>Uses only the incoming hashed credential, independent of cookie identity.</summary>
    private static HttpClient Bearer(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", userId);
        return client;
    }

    /// <summary>Seeds the established routed custom route, ordered waypoints, manual measurements and durable provider provenance.</summary>
    private async Task<SegmentSeed> SeedAsync()
    {
        var seed = await new TripEditorSegmentMutationPostgresTestSupport(fixture).SeedAsync();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { UserId = seed.UserId, User = null!, Name = "853 Slice 6", TokenHash = ApiTokenService.HashToken(seed.UserId) });
        await db.SaveChangesAsync();
        await db.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s
            .SetProperty(s => s.RouteProvider, "geoapify").SetProperty(s => s.RouteMappingMode, "walk")
            .SetProperty(s => s.RouteTransportProfileId, seed.FirstProfileId).SetProperty(s => s.RouteStorageMode, "retained")
            .SetProperty(s => s.RouteInstructionsJson, "[]").SetProperty(s => s.RouteAttribution, "Provider attribution")
            .SetProperty(s => s.RouteProviderConfigurationId, Guid.NewGuid()).SetProperty(s => s.RouteProviderConfigurationVersion, 3)
            .SetProperty(s => s.RouteGeneratedAt, DateTimeOffset.UtcNow));
        return seed;
    }

    /// <summary>Reads the actual published state, including server-issued opaque token and nullable original evidence.</summary>
    private static async Task<EditorSegmentDto> PublishedAsync(WebApplication app, SegmentSeed seed)
    {
        using var client = await WebAsync(app, seed.UserId);
        var state = await client.GetFromJsonAsync<EditorTripStateDto>($"/api/trips/{seed.TripId}/editor");
        return state!.SegmentsById[seed.SegmentId!.Value];
    }

    /// <summary>Retains the existing aggregate wire shape; no decomposition of combined Notes/route changes.</summary>
    private static object FullRequest(EditorSegmentDto dto, string? notes = null, double? duration = null) => new
    {
        dto.FromPlaceId, dto.ToPlaceId, dto.WaypointPlaceIds, dto.WaypointRouteVertexIndices, dto.Mode,
        dto.EstimatedDistanceKm, estimatedDurationMinutes = duration ?? dto.EstimatedDurationMinutes,
        dto.EstimatedDurationSource, notesHtml = notes ?? dto.NotesHtml, dto.Route, dto.AggregateConcurrencyToken
    };

    private static string WebUrl(SegmentSeed seed) => $"/api/trips/{seed.TripId}/editor/segments/{seed.SegmentId}";
    private static string PatchUrl(SegmentSeed seed, bool web) => web ? WebUrl(seed) + "/conditional"
        : $"/api/trips/{seed.TripId}/conditional/segments/{seed.SegmentId}";

    /// <summary>Submits independently present original Notes including null, with adapter-specific field spelling.</summary>
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, SegmentSeed seed, bool web, string? original, string? desired) =>
        client.PatchAsJsonAsync(PatchUrl(seed, web), new
        {
            expected = new Dictionary<string, object?> { [web ? "notesHtml" : "notes"] = original },
            changes = new Dictionary<string, object?> { [web ? "notesHtml" : "notes"] = desired }
        });

    /// <summary>Loads fresh persisted values rather than identity-map state.</summary>
    private static Task<Segment> ReadAsync(ApplicationDbContext db, SegmentSeed seed) =>
        TripEditorSegmentMutationPostgresTestSupport.ReadAsync(db, seed.SegmentId!.Value);

    /// <summary>Captures every Segment scalar, waypoint physical version and unrelated Trip scalar for zero-write assertions.</summary>
    private async Task<string> SnapshotAsync(SegmentSeed seed)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT jsonb_build_object('segment', to_jsonb(s) - 'Notes', 'notes', s."Notes", 'version', s.xmin::text,
              'waypoints', (SELECT jsonb_agg(to_jsonb(w) || jsonb_build_object('version', w.xmin::text) ORDER BY w."Position")
                FROM "SegmentWaypoints" w WHERE w."SegmentId" = s."Id"), 'trip', to_jsonb(t))::text
            FROM "Segments" s JOIN "Trips" t ON t."Id" = s."TripId" WHERE s."Id" = $1
            """, connection);
        command.Parameters.AddWithValue(seed.SegmentId!.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
