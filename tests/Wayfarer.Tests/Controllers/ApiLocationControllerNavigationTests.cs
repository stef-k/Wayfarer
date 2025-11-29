using Microsoft.AspNetCore.Http;
using System.Net;
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
/// Navigation availability API path.
/// </summary>
public class ApiLocationControllerNavigationTests : TestBase
{
    [Fact]
    public void CheckNavigationAvailability_Forbids_WhenInactive()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = false;
        db.SaveChanges();
        var controller = BuildController(db);
        var today = DateTime.Today;

        var result = controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day);

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

        var httpContext = BuildHttpContextWithUser("u1");
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
