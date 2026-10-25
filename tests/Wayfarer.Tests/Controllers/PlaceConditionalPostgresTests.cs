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

/// <summary>Routed PostgreSQL evidence for paired Place fields, exact originals and rejected unconditional writers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class PlaceConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Unconditional Web/bearer drafts cannot bypass selected originals; protected scalar changes retain disjoint Notes.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaleWebDraft_UnconditionalPathsReject(bool conditional, bool protectedBearer)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        using var mobile = protectedBearer
            ? await PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "Newer Mobile notes" })
            : await bearer.PutAsJsonAsync($"/api/trips/places/{place.Id}", new { notes = "Newer Mobile notes" });
        Assert.Equal(protectedBearer ? HttpStatusCode.OK : HttpStatusCode.Conflict, mobile.StatusCode);
        using var response = conditional
            ? protectedBearer
                ? await PatchAsync(web, place, true, new { iconName = place.IconName }, new { iconName = "star" })
                : await PatchAsync(web, place, true, new { name = place.Name }, new { name = "Web name" })
            : await web.PutAsJsonAsync($"/api/trips/{place.Region.TripId}/editor/places/{place.Id}", new
            { regionId = place.RegionId, name = "Web name", notesHtml = place.Notes, address = place.Address,
                location = new { latitude = 14, longitude = 22 }, iconName = "marker", markerColor = "bg-blue", reverseGeocode = false });
        Assert.True(response.StatusCode == (conditional ? HttpStatusCode.OK : HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(protectedBearer ? "Newer Mobile notes" : place.Notes, stored.Notes);
        Assert.Equal((conditional && !protectedBearer ? "Web name" : place.Name, place.Address, place.Location!.AsText(), place.RegionId, place.DisplayOrder),
            (stored.Name, stored.Address, stored.Location!.AsText(), stored.RegionId, stored.DisplayOrder));
        output.WriteLine("Protected disjoint fields survive; unsupported unconditional requests do not mutate.");
    }

    /// <summary>Disjoint Web Name/icon and bearer Notes/color survive both orders with exact SQL footprints.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisjointEdits_BothSaveOrders(bool webFirst)
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        async Task Save(bool isWeb)
        {
            using var response = isWeb
                ? await PatchAsync(web, place, true, new { name = place.Name, iconName = place.IconName }, new { name = "  Web name  ", iconName = "star" })
                : await PatchAsync(bearer, place, false, new { notes = place.Notes, markerColor = place.MarkerColor }, new { notes = "Mobile notes", markerColor = "bg-red" });
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (isWeb) Assert.Equal(result.GetProperty("data").GetRawText(), result.GetProperty("affected").GetProperty("places")[0].GetRawText());
            else Assert.Equal("bd2-1", result.GetProperty("contract").GetString());
        }
        await Save(webFirst);
        await Save(!webFirst);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(("Web name", "Mobile notes", "star", "bg-red", place.Address, place.RegionId, place.DisplayOrder),
            (stored.Name, stored.Notes, stored.IconName, stored.MarkerColor, stored.Address, stored.RegionId, stored.DisplayOrder));
        Assert.Equal(place.Location!.AsText(), stored.Location!.AsText());
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"RegionId\" =", sql.Split("WHERE")[0]));
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"Location\" =", sql));
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"Address\" =", sql));
        Assert.All(probe.Updates, sql => Assert.DoesNotContain("\"DisplayOrder\" =", sql));
        output.WriteLine($"Disjoint routed save order webFirst={webFirst}; no structural columns written.");
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Null Notes and nullable marker storage remain distinct from empty/default display values.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<P>Legacy <STRONG>notes</STRONG></P>")]
    public async Task ExactOriginals_AndReviewedBaseClear(string? notes)
    {
        var place = await SeedAsync(notes, null, null);
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        using var state = await web.GetAsync($"/api/trips/{place.Region.TripId}/editor");
        var dto = (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("placesById").GetProperty(place.Id.ToString());
        Assert.Equal("marker", dto.GetProperty("iconName").GetString());
        Assert.Equal("bg-blue", dto.GetProperty("markerColor").GetString());
        var original = dto.GetProperty("originalValues");
        Assert.Equal(place.RegionId, original.GetProperty("regionId").GetGuid());
        Assert.Equal(RichNotes.Normalize(notes), original.GetProperty("notesHtml").GetString());
        Assert.Equal(JsonValueKind.Null, original.GetProperty("iconName").ValueKind);
        Assert.Equal(JsonValueKind.Null, original.GetProperty("markerColor").ValueKind);
        using var falseBase = await PatchAsync(web, place, true, new { iconName = "marker" }, new { iconName = "star" });
        Assert.Equal(HttpStatusCode.Conflict, falseBase.StatusCode);
        using var icon = await PatchAsync(web, place, true, new { iconName = (string?)null, notesHtml = notes }, new { iconName = "star", notesHtml = (string?)null });
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        await using (var verify = fixture.CreateContext())
        {
            var saved = await verify.Places.SingleAsync(p => p.Id == place.Id);
            Assert.Equal((notes, "star", (string?)null), (saved.Notes, saved.IconName, saved.MarkerColor));
        }
        using var clear = await PatchAsync(bearer, place, false, new { notes = RichNotes.Normalize(notes), markerColor = (string?)null }, new { notes = "", markerColor = "bg-blue" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var noOp = await PatchAsync(bearer, place, false, new { notes = "" }, new { notes = "" });
        Assert.False((await noOp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        Assert.Single(_warmup.Invocations);
        await using var fresh = fixture.CreateContext();
        var current = await fresh.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(("", "star", "bg-blue"), (current.Notes, current.IconName, current.MarkerColor));
    }

    /// <summary>Stale Notes reject the entire request in either direction, then explicit current-base Clear succeeds.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameNotes_ConflictIsUnapplied_ThenRefreshedBaseSucceeds(bool webFirst)
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.Region.UserId);
        using var bearer = Bearer(app, place.Region.UserId);
        var firstKey = webFirst ? "notesHtml" : "notes";
        var secondKey = webFirst ? "notes" : "notesHtml";
        using var first = await PatchAsync(webFirst ? web : bearer, place, webFirst,
            new Dictionary<string, object?> { [firstKey] = place.Notes }, new Dictionary<string, object?> { [firstKey] = "Private newer notes" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var writes = probe.Updates.Count;
        var warmups = _warmup.Invocations.Count;
        var expected = new Dictionary<string, object?> { [secondKey] = place.Notes };
        var changes = new Dictionary<string, object?> { [secondKey] = "Stale notes" };
        if (!webFirst) { expected["name"] = place.Name; changes["name"] = "Must not commit"; }
        using var stale = await PatchAsync(webFirst ? bearer : web, place, !webFirst, expected, changes);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("mutation-base-stale", await stale.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Private newer notes", await stale.Content.ReadAsStringAsync());
        Assert.Equal(writes, probe.Updates.Count);
        Assert.Equal(warmups, _warmup.Invocations.Count);
        using var retry = await PatchAsync(webFirst ? bearer : web, place, !webFirst,
            new Dictionary<string, object?> { [secondKey] = "Private newer notes" }, new Dictionary<string, object?> { [secondKey] = "" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.SingleAsync(a => a.Id == place.Id);
        Assert.Equal((place.Name, ""), (stored.Name, stored.Notes));
        Assert.Equal(place.Location!.AsText(), stored.Location!.AsText());
    }

    /// <summary>Uses established routing/identity fixtures and fresh scoped database contexts; only external work is mocked.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor[]? interceptors = null, TimeProvider? clock = null) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors ?? []));
            services.AddSingleton(_warmup.Object);
            if (clock != null) services.AddScoped(sp => new LifecycleDependencyConfirmation(
                sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), clock));
            var webRoot = CreateTestDirectory();
            var icons = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "marker");
            Directory.CreateDirectory(icons);
            foreach (var name in new[] { "marker", "star" }) File.WriteAllText(Path.Combine(icons, name + ".svg"), "<svg/>");
            var colors = new Mock<IIconColorProvider>();
            colors.Setup(p => p.GetAvailableColors()).Returns(new IconColorClasses(["bg-blue", "bg-red"], []));
            services.AddSingleton(colors.Object);
            services.AddSingleton(Mock.Of<IWebHostEnvironment>(env => env.WebRootPath == webRoot));
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
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Place place, bool web, object expected, object changes)
    {
        var originals = JsonSerializer.SerializeToElement(expected).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        originals["regionId"] = place.RegionId;
        return client.PatchAsJsonAsync(web ? $"/api/trips/{place.Region.TripId}/editor/places/{place.Id}/conditional"
            : $"/api/trips/{place.Region.TripId}/conditional/places/{place.Id}", new { expected = originals, changes });
    }

    /// <summary>Seeds exact Place scalars and a credential, under the reusable fixture's scoped cleanup.</summary>
    private async Task<Place> SeedAsync(string? notes = "Original notes", string? icon = "marker", string? color = "bg-blue")
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Parent Trip", Notes = "Parent Notes", IsPublic = true };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "Original Region", DisplayOrder = 1 };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "Original Place", UserId = user.Id, Notes = notes,
            IconName = icon, MarkerColor = color, DisplayOrder = 7, Address = "Original address", Location = new Point(22, 14) { SRID = 4326 } };
        await using var db = fixture.CreateContext();
        db.Places.Add(place);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 7", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        return place;
    }

}
