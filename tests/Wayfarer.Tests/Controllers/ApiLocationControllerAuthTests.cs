using System.Net.Http;
using System.Threading;
using System.Net.Http;
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
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Location auth quick checks.
/// </summary>
public class ApiLocationControllerAuthTests : TestBase
{
    [Fact]
    public async Task CheckIn_ReturnsForbidden_WhenUserInactive()
    {
        var db = CreateDbContext();
        SeedSettings(db);
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = false;
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        Assert.IsType<ForbidResult>(result);
    }

    private LocationController BuildController(ApplicationDbContext db)
    {
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

        var httpContext = BuildHttpContextWithUser("u1", "User");
        httpContext.Request.Headers["Authorization"] = "Bearer tok";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: "u1");
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
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
