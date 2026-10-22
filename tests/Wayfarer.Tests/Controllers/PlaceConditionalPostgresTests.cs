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

/// <summary>Routed PostgreSQL evidence for paired Place fields, exact scalar originals and retained unconditional writers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class PlaceConditionalPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Routed stale complete Web drafts overwrite bearer Notes; scalar PATCH retains them.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleWebDraft_LegacyOverwriteAndProtectedCounterpart(bool conditional)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        using var mobile = await bearer.PutAsJsonAsync($"/api/trips/places/{place.Id}", new { notes = "Newer Mobile notes" });
        Assert.Equal(HttpStatusCode.OK, mobile.StatusCode);
        using var response = conditional
            ? await PatchAsync(web, place, true, new { name = place.Name }, new { name = "Web name" })
            : await web.PutAsJsonAsync($"/api/trips/{place.Region.TripId}/editor/places/{place.Id}", new
            { regionId = place.RegionId, name = "Web name", notesHtml = place.Notes, address = place.Address,
                location = new { latitude = 14, longitude = 22 }, iconName = "marker", markerColor = "bg-blue", reverseGeocode = false });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(conditional ? "Newer Mobile notes" : place.Notes, stored.Notes);
        Assert.Equal(("Web name", place.Address, place.Location!.AsText(), place.RegionId, place.DisplayOrder),
            (stored.Name, stored.Address, stored.Location!.AsText(), stored.RegionId, stored.DisplayOrder));
        output.WriteLine(conditional ? "PROTECTED: scalar Web Name preserves bearer Notes."
            : "LEGACY OVERWRITE: complete Web PUT restores stale Notes; retained compatibility risk.");
    }

    /// <summary>Uses established routing/identity fixtures and fresh scoped database contexts; only external work is mocked.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, IInterceptor[]? interceptors = null) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(interceptors ?? []));
            services.AddSingleton(_warmup.Object);
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
