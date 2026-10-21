using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises the paired metadata routes with real Identity, antiforgery and PostgreSQL commits.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class TripMetadataConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();
    private readonly Mock<ITripMapThumbnailGenerator> _thumbnails = new();

    /// <summary>Both clients retain their original read while the independent field commits first in either order.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DisjointOriginalDrafts_BothSurvive(bool webFirst, bool webNames)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, trip.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        using var bearer = app.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", trip.UserId);
        var webDraft = webNames
            ? new { expected = new Dictionary<string, object?> { ["name"] = trip.Name }, changes = new Dictionary<string, object?> { ["name"] = "Web name" } }
            : new { expected = new Dictionary<string, object?> { ["notesHtml"] = trip.Notes }, changes = new Dictionary<string, object?> { ["notesHtml"] = "Web notes" } };
        var mobileDraft = webNames
            ? new { expected = new Dictionary<string, object?> { ["notes"] = trip.Notes }, changes = new Dictionary<string, object?> { ["notes"] = "Mobile notes" } }
            : new { expected = new Dictionary<string, object?> { ["name"] = trip.Name }, changes = new Dictionary<string, object?> { ["name"] = "Mobile name" } };

        async Task SaveWeb()
        {
            using var response = await web.PatchAsJsonAsync($"/api/trips/{trip.Id}/editor/metadata/conditional", webDraft);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        async Task SaveBearer()
        {
            using var response = await bearer.PatchAsJsonAsync($"/api/trips/{trip.Id}/conditional", mobileDraft);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("bd2-1", result.GetProperty("contract").GetString());
            Assert.True(result.GetProperty("applied").GetBoolean());
        }

        if (webFirst) { await SaveWeb(); await SaveBearer(); }
        else { await SaveBearer(); await SaveWeb(); }
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(webNames ? "Web name" : "Mobile name", stored.Name);
        Assert.Equal(webNames ? "Mobile notes" : "Web notes", stored.Notes);
        Assert.Equal(trip.CoverImageUrl, stored.CoverImageUrl);
        Assert.True(stored.IsPublic && stored.ShareProgressEnabled);
    }

    /// <summary>Web-only metadata and derived privacy changes remain atomic and never replay Name/Notes.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebOnlyDraft_AndBearerNotes_BothSurvive(bool webFirst)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, trip.UserId);
        using var bearer = Bearer(app, trip.UserId);
        var expected = new { isPublic = true, coverImage = new { rawUrl = trip.CoverImageUrl }, center = new { latitude = 1, longitude = 2 }, zoom = 3 };
        var changes = new { isPublic = false, coverImage = new { rawUrl = "  https://example.test/new.jpg  " }, center = new { latitude = 0, longitude = 0 }, zoom = 0 };
        async Task SaveWeb()
        {
            using var response = await PatchAsync(web, trip.Id, true, expected, changes);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("share-progress-disabled", result.GetProperty("warnings")[0].GetProperty("code").GetString());
        }
        async Task SaveBearer()
        {
            using var response = await PatchAsync(bearer, trip.Id, false, new { notes = trip.Notes }, new { notes = "Mobile notes" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (webFirst) { await SaveWeb(); await SaveBearer(); }
        else { await SaveBearer(); await SaveWeb(); }
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(trip.Name, stored.Name);
        Assert.Equal("Mobile notes", stored.Notes);
        Assert.Equal("https://example.test/new.jpg", stored.CoverImageUrl);
        Assert.False(stored.IsPublic || stored.ShareProgressEnabled);
        Assert.Equal((0d, 0d, 0), (stored.CenterLat, stored.CenterLon, stored.Zoom));
        Assert.Single(_thumbnails.Invocations);
    }

    /// <summary>Stale same-field originals reject the entire draft; an explicitly reviewed new base still compares.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SameField_RejectsWithoutPartialEffects_ThenExplicitBaseSucceeds(bool webFirst, bool name)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, trip.UserId);
        using var bearer = Bearer(app, trip.UserId);
        var (firstClient, secondClient) = webFirst ? (web, bearer) : (bearer, web);
        var firstKey = name ? "name" : webFirst ? "notesHtml" : "notes";
        var secondKey = name ? "name" : webFirst ? "notes" : "notesHtml";
        var otherKey = name ? (webFirst ? "notes" : "notesHtml") : "name";
        var otherOriginal = name ? trip.Notes : trip.Name;
        var original = name ? trip.Name : trip.Notes;
        using var first = await PatchAsync(firstClient, trip.Id, webFirst,
            new Dictionary<string, object?> { [firstKey] = original }, new Dictionary<string, object?> { [firstKey] = "First edit" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using var snapshot = fixture.CreateContext();
        var before = await snapshot.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        var warmups = _warmup.Invocations.Count;
        var thumbnails = _thumbnails.Invocations.Count;
        using var stale = await PatchAsync(secondClient, trip.Id, !webFirst,
            new Dictionary<string, object?> { [secondKey] = original, [otherKey] = otherOriginal },
            new Dictionary<string, object?> { [secondKey] = "Stale edit", [otherKey] = "Must not commit" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadAsStringAsync();
        Assert.Contains("mutation-base-stale", problem);
        Assert.DoesNotContain("First edit", problem);
        await using var verification = fixture.CreateContext();
        var after = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(warmups, _warmup.Invocations.Count);
        Assert.Equal(thumbnails, _thumbnails.Invocations.Count);
        using var resolved = await PatchAsync(secondClient, trip.Id, !webFirst,
            new Dictionary<string, object?> { [secondKey] = "First edit" }, new Dictionary<string, object?> { [secondKey] = name ? "Reviewed name" : "" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var stored = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(name ? "Reviewed name" : "", name ? stored.Name : stored.Notes);
    }

    /// <summary>Original Notes distinguish null/empty, compare published normalization, and permit identity-equal controls.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<P>Original <STRONG>notes</STRONG></P>")]
    public async Task NotesOriginals_ClearAndNormalizedIdentity(string? notes)
    {
        var trip = await SeedAsync(notes);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, trip.UserId);
        using var bearer = Bearer(app, trip.UserId);
        // The mapped display is intentionally lossy. The additive original is normalized and nullable.
        var metadata = Wayfarer.Models.Dtos.Editor.EditorTripStateMapper.ToMetadata(trip, null, null);
        Assert.Equal(RichNotes.Normalize(notes), metadata.OriginalValues!.NotesHtml);
        Assert.Equal(RichNotes.Normalize(notes) ?? "", metadata.NotesHtml);
        using var response = await PatchAsync(web, trip.Id, true, new { notesHtml = metadata.OriginalValues.NotesHtml }, new { notesHtml = "" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verification = fixture.CreateContext();
        Assert.Equal("", (await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id)).Notes);
        var warmed = _warmup.Invocations.Count;
        using var identity = await PatchAsync(bearer, trip.Id, false, new { notes = "" }, new { notes = "" });
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.False((await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        Assert.Equal(warmed, _warmup.Invocations.Count);
        using var staleNull = await PatchAsync(bearer, trip.Id, false, new { notes = (string?)null }, new { notes = "Should conflict" });
        Assert.Equal(HttpStatusCode.Conflict, staleNull.StatusCode);
    }

    /// <summary>Malformed, unknown, duplicate and missing-original inputs never become unconditional writes.</summary>
    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("{\"expected\":{},\"changes\":{\"name\":\"new\"}}")]
    [InlineData("{\"expected\":{},\"changes\":{}}")]
    [InlineData("{\"expected\":{\"notes\":null},\"changes\":{\"notes\":null}}")]
    [InlineData("{\"expected\":{\"name\":\"Original name\"},\"changes\":{\"name\":\"new\",\"name\":\"other\"}}")]
    [InlineData("{\"expected\":{\"name\":\"Original name\"},\"changes\":{\"name\":\"new\"},\"extra\":1}")]
    [InlineData("{\"expected\":{\"notes\":\"Original notes\"},\"changes\":{\"notes\":\"<img src='data:image/png;base64,a'>\"}}")]
    public async Task StrictRequest_RejectsWithoutEffects(string json)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var bearer = Bearer(app, trip.UserId);
        using var response = await bearer.PatchAsync($"/api/trips/{trip.Id}/conditional", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("mutation-request-invalid", await response.Content.ReadAsStringAsync());
        await using var verification = fixture.CreateContext();
        Assert.Equal(trip.Name, (await verification.Trips.FindAsync(trip.Id))!.Name);
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>Null desired Notes neither compares nor writes that field while another dirty field commits.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullDesiredNotes_PreservesInterveningNotes_AndOriginalViewEvidence(bool suppliedNotesBase)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Notes, "New Notes")
            .SetProperty(t => t.CenterLon, (double?)null));
        await using var app = await StartAsync(db);
        using var bearer = Bearer(app, trip.UserId);
        var expected = new Dictionary<string, object?> { ["name"] = trip.Name };
        if (suppliedNotesBase) expected["notes"] = trip.Notes;
        using var response = await PatchAsync(bearer, trip.Id, false, expected,
            new { name = "  New name  ", notes = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var web = await WebAsync(app, trip.UserId);
        using var view = await PatchAsync(web, trip.Id, true, new { center = new { latitude = 1, longitude = (double?)null } },
            new { center = (object?)null });
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        var result = await view.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Notes", result.GetProperty("data").GetProperty("originalValues").GetProperty("notesHtml").GetString());
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal("New name", stored.Name);
        Assert.Equal("New Notes", stored.Notes);
        Assert.Null(stored.CenterLat);
        Assert.Null(stored.CenterLon);
    }

    /// <summary>Registers per-request contexts while keeping production routing and authority intact.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor[]? interceptors = null, Action<WebApplication>? middleware = null) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors ?? []));
            services.AddSingleton(_warmup.Object);
            services.AddSingleton(_thumbnails.Object);
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        }, middleware);

    /// <summary>Uses a genuine User cookie and its matching antiforgery token.</summary>
    private static async Task<HttpClient> WebAsync(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, userId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        return client;
    }

    /// <summary>Uses the seeded incoming credential without cookie authority.</summary>
    private static HttpClient Bearer(WebApplication app, string userId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", userId);
        return client;
    }

    /// <summary>Sends only the selected original/dirty-field contract through its actual route.</summary>
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid tripId, bool web, object expected, object changes) =>
        client.PatchAsJsonAsync($"/api/trips/{tripId}" + (web ? "/editor/metadata/conditional" : "/conditional"), new { expected, changes });

    /// <summary>Seeds a separately owned Trip and an actual accepted incoming bearer, with scoped cleanup.</summary>
    private async Task<Trip> SeedAsync(string? notes = "Original notes")
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Original name", Notes = notes,
            IsPublic = true, ShareProgressEnabled = true, CoverImageUrl = "https://example.test/before.jpg",
            CenterLat = 1, CenterLon = 2, Zoom = 3 };
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 3", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        return trip;
    }
}
