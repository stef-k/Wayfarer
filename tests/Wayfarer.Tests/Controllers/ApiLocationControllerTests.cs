using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API-facing location tests (bulk delete and navigation flags).
/// </summary>
public class ApiLocationControllerTests : TestBase
{
    [Fact]
    public async Task BulkDelete_RemovesOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        var other = TestDataFixtures.CreateUser(id: "api-other", username: "api-other");
        db.Users.AddRange(user, other);

        var userLocation1 = CreateLocation(user.Id, 1);
        var userLocation2 = CreateLocation(user.Id, 2);
        var otherLocation = CreateLocation(other.Id, 3);

        db.Locations.AddRange(userLocation1, userLocation2, otherLocation);
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var request = new LocationController.BulkDeleteRequest
        {
            LocationIds = new List<int> { userLocation1.Id, userLocation2.Id, otherLocation.Id }
        };

        var result = await controller.BulkDelete(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("2 locations deleted", payload);

        Assert.DoesNotContain(db.Locations, l => l.Id == userLocation1.Id);
        Assert.DoesNotContain(db.Locations, l => l.Id == userLocation2.Id);
        Assert.Contains(db.Locations, l => l.Id == otherLocation.Id);
    }

    [Fact]
    public async Task BulkDelete_ReturnsNotFoundWhenNoMatchingIds()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.Locations.Add(CreateLocation(user.Id, 10));
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);
        var request = new LocationController.BulkDeleteRequest
        {
            LocationIds = new List<int> { 999, 1000 }
        };

        var result = await controller.BulkDelete(request);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("No locations found", payload);
        Assert.Single(db.Locations);
    }

    [Fact]
    public async Task Delete_RemovesSingleLocationForOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        var other = TestDataFixtures.CreateUser(id: "api-other", username: "api-other");
        db.Users.AddRange(user, other);

        var ownLoc = CreateLocation(user.Id, 50);
        var otherLoc = CreateLocation(other.Id, 60);
        db.Locations.AddRange(ownLoc, otherLoc);
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var result = await controller.Delete(ownLoc.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Location deleted", payload);
        Assert.DoesNotContain(db.Locations, l => l.Id == ownLoc.Id);
        Assert.Contains(db.Locations, l => l.Id == otherLoc.Id);
    }

    [Fact]
    public async Task CheckNavigationAvailability_ReturnsUnauthorized_WhenTokenMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.SaveChanges();

        var controller = BuildApiController(db, user, includeAuthHeader: false);

        var result = await controller.CheckNavigationAvailability("day", DateTime.UtcNow.Year, 1, 1);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CheckNavigationAvailability_ReturnsFlags_ForCurrentDay()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "tok", UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var controller = BuildApiController(db, user, tokenOverride: "tok");
        var today = DateTime.Today;

        var result = await controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        bool? canPrev = payload.GetType().GetProperty("canNavigatePrevDay")?.GetValue(payload) as bool?;
        bool? canNext = payload.GetType().GetProperty("canNavigateNextDay")?.GetValue(payload) as bool?;
        Assert.True(canPrev);
        Assert.False(canNext);
    }

    private static LocationController BuildApiController(ApplicationDbContext db, ApplicationUser user)
        => BuildApiController(db, user, includeAuthHeader: true, tokenOverride: null);

    private static LocationController BuildApiController(ApplicationDbContext db, ApplicationUser user, bool includeAuthHeader, string? tokenOverride = null)
    {
        var token = tokenOverride ?? $"token-{user.Id}";
        if (!db.ApiTokens.Any(t => t.Token == token))
        {
            db.ApiTokens.Add(new ApiToken
            {
                Name = "mobile",
                Token = token,
                UserId = user.Id,
                User = user,
                CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ApplicationSettingsService(db, cache);
        var reverseGeocoding = new ReverseGeocodingService(new HttpClient(new FakeHandler()), NullLogger<BaseApiController>.Instance);
        var locationService = new LocationService(db);
        var sse = new SseService();
        var stats = new LocationStatsService(db);

        var controller = new LocationController(
            db,
            NullLogger<BaseApiController>.Instance,
            cache,
            settings,
            reverseGeocoding,
            locationService,
            sse,
            stats,
            locationService);

        var httpContext = new DefaultHttpContext();
        if (includeAuthHeader)
        {
            httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id)
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static Wayfarer.Models.Location CreateLocation(string userId, int seed)
    {
        return new Wayfarer.Models.Location
        {
            Id = seed,
            UserId = userId,
            Coordinates = new Point(seed, seed) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC"
        };
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
