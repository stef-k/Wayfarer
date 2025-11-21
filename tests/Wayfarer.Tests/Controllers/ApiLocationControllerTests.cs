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
/// API-facing location tests (bulk delete).
/// </summary>
public class ApiLocationControllerTests : TestBase
{
    [Fact]
    public async Task BulkDelete_RemovesOnlyCurrentUserLocations()
    {
        // Arrange
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

        // Act
        var result = await controller.BulkDelete(request);

        // Assert
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
        // Arrange
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

        // Act
        var result = await controller.BulkDelete(request);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("No locations found", payload);
        Assert.Single(db.Locations); // original still present
    }

    [Fact]
    public async Task Delete_RemovesSingleLocationForOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        var other = TestDataFixtures.CreateUser(id: "api-other", username: "api-other");
        db.Users.AddRange(user, other);

        var ownLoc = CreateLocation(user.Id, 50);
        var otherLoc = CreateLocation(other.Id, 60);
        db.Locations.AddRange(ownLoc, otherLoc);
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        // Act
        var result = await controller.Delete(ownLoc.Id);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Location deleted", payload);
        Assert.DoesNotContain(db.Locations, l => l.Id == ownLoc.Id);
        Assert.Contains(db.Locations, l => l.Id == otherLoc.Id);
    }

    private static LocationController BuildApiController(ApplicationDbContext db, ApplicationUser user)
    {
        var token = $"token-{user.Id}";
        db.ApiTokens.Add(new ApiToken
        {
            Name = "mobile",
            Token = token,
            UserId = user.Id,
            User = user,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

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
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
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

    private static ApplicationUser CreateUser(string id, string username)
    {
        return new ApplicationUser
        {
            Id = id,
            UserName = username,
            DisplayName = username,
            Email = $"{username}@test.com",
            EmailConfirmed = true,
            IsActive = true
        };
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
