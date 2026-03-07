using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for direct-serve public trip image endpoints (CoverImage and MapSnapshot).
/// </summary>
public class PublicTripImagesTests : TestBase
{
    [Fact]
    public async Task CoverImage_ReturnsRedirect_ForPublicTripWithCoverImage()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Public Trip",
            IsPublic = true, CoverImageUrl = coverUrl, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetCoverImage(tripId);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(coverUrl, redirect.Url);
        Assert.False(redirect.Permanent);
    }

    [Fact]
    public async Task CoverImage_ReturnsNotFound_ForPublicTripWithoutCoverImage()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "No Cover",
            IsPublic = true, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetCoverImage(tripId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CoverImage_ReturnsNotFound_ForPrivateTrip()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Private Trip",
            IsPublic = false, CoverImageUrl = "https://example.com/cover.jpg",
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetCoverImage(tripId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CoverImage_ReturnsNotFound_ForNonexistentTrip()
    {
        var controller = BuildController(CreateDbContext());
        var result = await controller.GetCoverImage(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MapSnapshot_ReturnsNotFound_ForTripWithoutCoordinates()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "No Coords",
            IsPublic = true, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetMapSnapshot(tripId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MapSnapshot_ReturnsNotFound_ForPrivateTrip()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Private",
            IsPublic = false, CenterLat = 40.0, CenterLon = 25.0, Zoom = 10,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetMapSnapshot(tripId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MapSnapshot_ReturnsNotFound_WhenThumbnailReturnsDataUri()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "DataURI",
            IsPublic = true, CenterLat = 40.0, CenterLon = 25.0, Zoom = 10,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        // Thumbnail service returns a data URI (placeholder SVG)
        var thumbMock = new Mock<ITripThumbnailService>();
        thumbMock.Setup(s => s.GetThumbUrlAsync(
                tripId, 40.0, 25.0, 10, null, It.IsAny<DateTime>(), "800x450", default))
            .ReturnsAsync("data:image/svg+xml,...");

        var controller = BuildController(db, thumbnailService: thumbMock.Object);
        var result = await controller.GetMapSnapshot(tripId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CoverImage_Returns429_WhenRateLimitExceeded()
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
        // Set a unique IP per test to avoid cross-test pollution
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50");

        // First request should succeed
        var result1 = await controller.GetCoverImage(tripId);
        Assert.IsType<RedirectResult>(result1);

        // Second request should be rate limited
        var result2 = await controller.GetCoverImage(tripId);
        var status = Assert.IsType<ObjectResult>(result2);
        Assert.Equal(429, status.StatusCode);
    }

    private TripViewerController BuildController(
        ApplicationDbContext db,
        ITripThumbnailService? thumbnailService = null,
        IApplicationSettingsService? settingsService = null)
    {
        var client = new System.Net.Http.HttpClient();
        thumbnailService ??= Mock.Of<ITripThumbnailService>();
        var tagService = Mock.Of<ITripTagService>();
        var imageCacheService = Mock.Of<IProxiedImageCacheService>();
        if (settingsService == null)
        {
            var settingsMock = new Mock<IApplicationSettingsService>();
            settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
            settingsService = settingsMock.Object;
        }
        var controller = new TripViewerController(
            NullLogger<TripViewerController>.Instance,
            db,
            client,
            thumbnailService,
            tagService,
            imageCacheService,
            settingsService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }
}
