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
/// API Location log-location endpoint basics.
/// </summary>
public class ApiLocationControllerLogTests : TestBase
{
    [Fact]
    public async Task LogLocation_ReturnsUnauthorized_WhenTokenMissing()
    {
        var controller = BuildController(CreateDbContext(), includeAuth: false);

        var result = await controller.LogLocation(new GpsLoggerLocationDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task LogLocation_ReturnsBadRequest_ForOutOfRangeCoords()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.LogLocation(new GpsLoggerLocationDto { Latitude = 200, Longitude = 10, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task LogLocation_CreatesLocation_ForValidInput()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.LogLocation(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count());
    }

    private LocationController BuildController(ApplicationDbContext db, bool includeAuth = true)
    {
        var user = SeedUserWithToken(db, "tok");
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
        if (includeAuth)
        {
            httpContext.Request.Headers["Authorization"] = "Bearer tok";
            httpContext.User = BuildPrincipal(user.Id, "User");
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }
}
