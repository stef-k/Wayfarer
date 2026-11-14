using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripCompositionHttpPostgresTests
{
    /// <summary>Web discovery is eligibility-only; the coordinate adapter issues the domain proof used by atomic composition apply.</summary>
    [PostgresFact]
    public async Task LayoutDiscoveryAndCoordinatePreviewRetainAuthorityPrivacyAndIntent()
    {
        var owner = await fixture.CreateUserAsync(); var foreign = await fixture.CreateUserAsync();
        var prefix = Guid.NewGuid().ToString("N");
        var a = Source(owner.Id, prefix + " A", false, null, 23.73);
        var b = Source(foreign.Id, prefix + " B", true, "CC-BY-4.0", 23.731);
        b.Planning = new(1, [new(Guid.NewGuid(), new("unknown"), "foreign-hidden-planning")]);
        var blocked = Source(foreign.Id, prefix + " view only", true, "view-only", 23.73);
        var hidden = Source(foreign.Id, prefix + " private", false, "CC0-1.0", 23.73);
        await using var db = fixture.CreateContext();
        db.AddRange(a, b, blocked, hidden); await db.SaveChangesAsync();
        foreach (var trip in new[] { a, b, blocked, hidden }) fixture.RegisterTrip(trip.Id);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        });
        using var browser = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(browser, app.Services, owner.Id);
        using var found = await browser.GetAsync($"/api/trip-compositions/sources?search={prefix}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode); Assert.True(found.Headers.CacheControl!.NoStore);
        var body = await found.Content.ReadAsStringAsync();
        Assert.DoesNotContain("planning", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreign.Id, body); Assert.DoesNotContain(blocked.Id.ToString(), body); Assert.DoesNotContain(hidden.Id.ToString(), body);
        using var sources = JsonDocument.Parse(body);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), sources.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
        using var own = await browser.GetAsync($"/api/trip-compositions/sources?search={prefix}&ownedOnly=true");
        Assert.Contains(a.Id.ToString(), await own.Content.ReadAsStringAsync()); Assert.DoesNotContain(b.Id.ToString(), await own.Content.ReadAsStringAsync());
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(browser);
        using var started = await browser.PostAsJsonAsync("/api/trip-compositions", new { sourceA = a.Id, sourceB = b.Id });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var start = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var c = start.RootElement.GetProperty("tripId").GetGuid(); fixture.RegisterTrip(c);
        var composition = start.RootElement.GetProperty("compositionId").GetGuid();
        await using var verify = fixture.CreateContext();
        var target = await verify.Places.AsNoTracking().SingleAsync(p => p.Region.TripId == c);
        var endpoint = $"/api/trips/{c}/editor/places/{target.Id}/coordinates/prepare";
        var preparation = new { expected = new { regionId = target.RegionId, location = new { latitude = 37.98, longitude = 23.73 } }, location = new { latitude = 37.98, longitude = 23.731 } };
        using var noCsrf = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(noCsrf, app.Services, owner.Id);
        using var csrfRefusal = await noCsrf.PostAsJsonAsync(endpoint, preparation); Assert.Equal(HttpStatusCode.BadRequest, csrfRefusal.StatusCode);
        using var other = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(other, app.Services, foreign.Id);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(other);
        using var denied = await other.PostAsJsonAsync(endpoint, preparation); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var preview = await browser.PostAsJsonAsync(endpoint, preparation);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode); Assert.True(preview.Headers.CacheControl!.NoStore);
        using var proof = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("coordinates", proof.RootElement.GetProperty("operation").GetString());
        var condition = proof.RootElement.GetProperty("conditionToken").GetString();
        var sourcePlace = b.Regions.Single().Places.Single();
        object Action(string operation, double[]? value = null) => new { source = "b", items = new[] { new {
            kind = "place", sourceId = sourcePlace.Id, decision = "reuse", destinationId = target.Id, expectedParentId = target.RegionId,
            conditionToken = condition, fields = operation == "edit" ? (object)new { location = new { operation, expected = new[] { 23.73, 37.98 }, value } }
                : new { location = new { operation, expected = new[] { 23.73, 37.98 } } }
        } } };
        using var anotherEffect = await browser.PostAsJsonAsync($"/api/trip-compositions/{composition}/actions", Action("edit", [24, 38]));
        Assert.Equal(HttpStatusCode.Conflict, anotherEffect.StatusCode);
        using var applied = await browser.PostAsJsonAsync($"/api/trip-compositions/{composition}/actions", Action("useSource"));
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        await using var reread = fixture.CreateContext();
        Assert.Equal(23.731, (await reread.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id)).Location!.X);
        Assert.Equal(23.73, (await reread.Places.AsNoTracking().SingleAsync(p => p.Id == a.Regions.Single().Places.Single().Id)).Location!.X);
        Assert.Equal(23.731, (await reread.Places.AsNoTracking().SingleAsync(p => p.Id == sourcePlace.Id)).Location!.X);
    }

    /// <summary>Minimal located sources expose no provider/routing setup and leave actual copy identity to production.</summary>
    private static Trip Source(string owner, string name, bool isPublic, string? policy, double longitude)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner, Name = name, IsPublic = isPublic, ContributionPolicy = policy };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = owner, Name = "Normal region" };
        region.Places.Add(new Place { Id = Guid.NewGuid(), Region = region, UserId = owner, Name = "Point", Location = new Point(longitude, 37.98) { SRID = 4326 } });
        trip.Regions.Add(region); return trip;
    }
}
