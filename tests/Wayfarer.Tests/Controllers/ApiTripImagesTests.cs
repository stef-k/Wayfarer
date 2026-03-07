using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the JSON metadata endpoint GET /api/trips/public/{id}/images.
/// </summary>
public class ApiTripImagesTests : TestBase
{
    [Fact]
    public async Task GetPublicTripImages_ReturnsJsonWithBothUrls_ForPublicTrip()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Full Trip",
            IsPublic = true, CoverImageUrl = "https://example.com/cover.jpg",
            CenterLat = 40.0, CenterLon = 25.0, Zoom = 10,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetPublicTripImages(tripId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = ok.Value!;
        var tripIdProp = json.GetType().GetProperty("tripId")!.GetValue(json);
        var coverUrl = json.GetType().GetProperty("coverImageUrl")!.GetValue(json) as string;
        var mapUrl = json.GetType().GetProperty("mapSnapshotUrl")!.GetValue(json) as string;

        Assert.Equal(tripId, tripIdProp);
        Assert.NotNull(coverUrl);
        Assert.Contains($"/Public/Trips/{tripId}/CoverImage", coverUrl);
        Assert.NotNull(mapUrl);
        Assert.Contains($"/Public/Trips/{tripId}/MapSnapshot", mapUrl);
    }

    [Fact]
    public async Task GetPublicTripImages_ReturnsNullCoverImageUrl_WhenNoCoverImage()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "No Cover",
            IsPublic = true, CenterLat = 40.0, CenterLon = 25.0, Zoom = 10,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetPublicTripImages(tripId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = ok.Value!;
        var coverUrl = json.GetType().GetProperty("coverImageUrl")!.GetValue(json);
        var mapUrl = json.GetType().GetProperty("mapSnapshotUrl")!.GetValue(json) as string;

        Assert.Null(coverUrl);
        Assert.NotNull(mapUrl);
    }

    [Fact]
    public async Task GetPublicTripImages_ReturnsNullMapSnapshotUrl_WhenNoCoordinates()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "No Coords",
            IsPublic = true, CoverImageUrl = "https://example.com/cover.jpg",
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetPublicTripImages(tripId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = ok.Value!;
        var coverUrl = json.GetType().GetProperty("coverImageUrl")!.GetValue(json) as string;
        var mapUrl = json.GetType().GetProperty("mapSnapshotUrl")!.GetValue(json);

        Assert.NotNull(coverUrl);
        Assert.Null(mapUrl);
    }

    [Fact]
    public async Task GetPublicTripImages_ReturnsNotFound_ForPrivateTrip()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Private",
            IsPublic = false, CoverImageUrl = "https://example.com/cover.jpg",
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetPublicTripImages(tripId);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicTripImages_ReturnsNotFound_ForNonexistentTrip()
    {
        var controller = BuildController(CreateDbContext());
        var result = await controller.GetPublicTripImages(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicTripImages_Returns429_WhenRateLimitExceeded()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "RL Trip",
            IsPublic = true, CoverImageUrl = "https://example.com/cover.jpg",
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            ProxyImageRateLimitEnabled = true,
            ProxyImageRateLimitPerMinute = 1
        });

        var controller = BuildController(db, settingsService: settingsMock.Object);
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.60");

        // First request should succeed
        var result1 = await controller.GetPublicTripImages(tripId);
        Assert.IsType<OkObjectResult>(result1);

        // Second request should be rate limited
        var result2 = await controller.GetPublicTripImages(tripId);
        var status = Assert.IsType<ObjectResult>(result2);
        Assert.Equal(429, status.StatusCode);
    }

    private TripsController BuildController(
        ApplicationDbContext db,
        IApplicationSettingsService? settingsService = null)
    {
        var tagService = Mock.Of<ITripTagService>();
        if (settingsService == null)
        {
            var settingsMock = new Mock<IApplicationSettingsService>();
            settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
            settingsService = settingsMock.Object;
        }
        var controller = new TripsController(
            db,
            NullLogger<BaseApiController>.Instance,
            tagService,
            settingsService,
            Mock.Of<ICacheWarmupScheduler>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Request = { Scheme = "https", Host = new HostString("example.com") }
            }
        };
        return controller;
    }
}
