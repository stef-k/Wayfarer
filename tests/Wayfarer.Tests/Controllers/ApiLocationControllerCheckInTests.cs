using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Mocks;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API LocationController check-in/log-location inputs.
/// </summary>
public class ApiLocationControllerCheckInTests : TestBase
{
    [Fact]
    public async Task CheckIn_ReturnsUnauthorized_WhenTokenMissing()
    {
        var controller = BuildController(CreateDbContext(), includeAuth: false);

        var result = await controller.CheckIn(new GpsLoggerLocationDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_ReturnsBadRequest_ForOutOfRangeCoords()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 200, Longitude = 10, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_CreatesLocation_ForValidInput()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count());
    }

    /// <summary>
    /// Tests that repeated idempotency keys bypass rate limits and reuse the original location.
    /// </summary>
    [Fact]
    public async Task CheckIn_ReturnsExisting_WhenIdempotencyKeyRepeated()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        var idempotencyKey = Guid.NewGuid();
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = idempotencyKey.ToString();

        var firstResult = await controller.CheckIn(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
        });

        var firstOk = Assert.IsType<OkObjectResult>(firstResult);
        Assert.Equal(1, db.Locations.Count());

        var secondResult = await controller.CheckIn(new GpsLoggerLocationDto
        {
            Latitude = 11,
            Longitude = 21,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(1), DateTimeKind.Utc)
        });

        var secondOk = Assert.IsType<OkObjectResult>(secondResult);
        Assert.Equal(1, db.Locations.Count());

        var firstLocation = (Location)firstOk.Value!.GetType().GetProperty("Location")!.GetValue(firstOk.Value)!;
        var secondLocation = (Location)secondOk.Value!.GetType().GetProperty("Location")!.GetValue(secondOk.Value)!;
        Assert.Equal(firstLocation.Id, secondLocation.Id);
    }

    [Fact]
    public async Task CheckIn_ReturnsTooManyRequests_WhenRateLimitedByTime()
    {
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = BuildController(db, includeAuth: true, cache: cache);
        var userId = db.Users.Single().Id;
        cache.Set($"lastCheckIn_{userId}", DateTime.UtcNow, TimeSpan.FromMinutes(5));

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var tooMany = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, tooMany.StatusCode);
    }

    [Fact]
    public async Task CheckIn_ReturnsTooManyRequests_WhenHourlyLimitExceeded()
    {
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = BuildController(db, includeAuth: true, cache: cache);
        var userId = db.Users.Single().Id;
        cache.Set($"checkInCount_{userId}_{DateTime.UtcNow:yyyyMMddHH}", 60, TimeSpan.FromHours(1));

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var tooMany = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, tooMany.StatusCode);
    }

    private LocationController BuildController(ApplicationDbContext db, bool includeAuth = true, IMemoryCache? cache = null)
    {
        SeedSettings(db);
        var user = SeedUserWithToken(db, "tok");
        cache ??= new MemoryCache(new MemoryCacheOptions());
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
            locationService,
            new NullPlaceVisitDetectionService());

        var httpContext = new DefaultHttpContext();
        if (includeAuth)
        {
            httpContext.Request.Headers["Authorization"] = "Bearer tok";
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id)
            }, "TestAuth"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: "checkin-user", username: "checkin");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        return user;
    }

    private static void SeedSettings(ApplicationDbContext db)
    {
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        db.SaveChanges();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
