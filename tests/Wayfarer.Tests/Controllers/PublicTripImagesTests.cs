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
    public async Task CoverImage_ReturnsFile_ForPublicTripWithCachedCoverImage()
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

        // Mock cache to return a hit so the endpoint serves bytes
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(((byte[] Bytes, string ContentType)?)(new byte[] { 0xFF, 0xD8 }, "image/jpeg"));

        var controller = BuildController(db, imageCacheService: cacheMock.Object);
        var result = await controller.GetCoverImage(tripId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
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
    public async Task CoverImage_Returns304_WithETag()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "ETag Trip",
            IsPublic = true, CoverImageUrl = coverUrl, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var cacheKey = Wayfarer.Util.ImageProxyHelper.ComputeImageCacheKey(coverUrl, null, null, null, true);
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext.Request.Headers["If-None-Match"] = $"\"{cacheKey}\"";

        var result = await controller.GetCoverImage(tripId);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(304, status.StatusCode);
    }

    [Fact]
    public async Task CoverImage_CacheControlUsesSettingsExpiryDays()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "Cache Trip",
            IsPublic = true, CoverImageUrl = coverUrl, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            ImageCacheExpiryDays = 30
        });

        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(((byte[] Bytes, string ContentType)?)(new byte[] { 0xFF, 0xD8 }, "image/jpeg"));

        var controller = BuildController(db, settingsService: settingsMock.Object, imageCacheService: cacheMock.Object);
        var result = await controller.GetCoverImage(tripId);

        Assert.IsType<FileContentResult>(result);
        var cacheControl = controller.Response.Headers["Cache-Control"].ToString();
        // 30 days = 2592000 seconds
        Assert.Contains("max-age=2592000", cacheControl);
    }

    [Fact]
    public async Task CoverImage_ReturnsBadRequest_ForDisallowedUrl()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "SSRF Trip",
            IsPublic = true, CoverImageUrl = "http://localhost/evil.jpg", UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var controller = BuildController(db);
        var result = await controller.GetCoverImage(tripId);

        Assert.IsType<BadRequestObjectResult>(result);
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

        // Mock cache to return a hit so the first request succeeds
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(((byte[] Bytes, string ContentType)?)(new byte[] { 0xFF, 0xD8 }, "image/jpeg"));

        var controller = BuildController(db, settingsService: settingsMock.Object, imageCacheService: cacheMock.Object);
        // Set a unique IP per test to avoid cross-test pollution
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50");

        // First request should succeed (served from cache)
        var result1 = await controller.GetCoverImage(tripId);
        Assert.IsType<FileContentResult>(result1);

        // Second request should be rate limited
        var result2 = await controller.GetCoverImage(tripId);
        var status = Assert.IsType<ObjectResult>(result2);
        Assert.Equal(429, status.StatusCode);
    }

    [Fact]
    public async Task MapSnapshot_ReturnsFile_WhenThumbnailUrlHasQueryString()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        db.Trips.Add(new Trip
        {
            Id = tripId, UserId = "owner", Name = "QS Trip",
            IsPublic = true, CenterLat = 40.0, CenterLon = 25.0, Zoom = 10,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        // Create a temp thumbnail file in wwwroot/thumbs/trips/
        var thumbDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "thumbs", "trips");
        Directory.CreateDirectory(thumbDir);
        var thumbFile = Path.Combine(thumbDir, $"{tripId}-800x450.jpg");

        try
        {
            // Write a minimal JPEG header so the file exists
            await System.IO.File.WriteAllBytesAsync(thumbFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

            // Mock thumbnail service to return URL with cache-busting query string
            var thumbMock = new Mock<ITripThumbnailService>();
            thumbMock.Setup(s => s.GetThumbUrlAsync(
                    tripId, 40.0, 25.0, 10, null, It.IsAny<DateTime>(), "800x450", default))
                .ReturnsAsync($"/thumbs/trips/{tripId}-800x450.jpg?v=638770000000000000");

            var controller = BuildController(db, thumbnailService: thumbMock.Object);
            var result = await controller.GetMapSnapshot(tripId);

            var file = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/jpeg", file.ContentType);
        }
        finally
        {
            if (System.IO.File.Exists(thumbFile))
                System.IO.File.Delete(thumbFile);
        }
    }

    private TripViewerController BuildController(
        ApplicationDbContext db,
        ITripThumbnailService? thumbnailService = null,
        IApplicationSettingsService? settingsService = null,
        IProxiedImageCacheService? imageCacheService = null)
    {
        var client = new System.Net.Http.HttpClient();
        thumbnailService ??= Mock.Of<ITripThumbnailService>();
        var tagService = Mock.Of<ITripTagService>();
        imageCacheService ??= Mock.Of<IProxiedImageCacheService>();
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
