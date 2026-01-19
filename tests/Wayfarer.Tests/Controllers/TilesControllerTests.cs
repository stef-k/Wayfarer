using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Public tiles endpoint behavior.
/// </summary>
public class TilesControllerTests : TestBase
{
    [Fact]
    public async Task GetTile_UnauthorizedWithoutReferer()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var controller = BuildController(cacheDir: cacheDir);

        var result = await controller.GetTile(1, 2, 3);

        try
        {
            var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal("Unauthorized request.", unauthorized.Value);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public async Task GetTile_NotFoundWhenServiceReturnsNull()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound);
        var controller = BuildController(handler: handler, cacheDir: cacheDir);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        var result = await controller.GetTile(1, 2, 3);

        try
        {
            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Tile not found.", notFound.Value);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public async Task GetTile_ReturnsPng_WhenCached()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        var tilePath = Path.Combine(cacheDir, "1_2_3.png");
        await File.WriteAllBytesAsync(tilePath, new byte[] { 1, 2, 3 });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, new byte[] { 9, 9, 9 });
        var controller = BuildController(handler: handler, cacheDir: cacheDir);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        var result = await controller.GetTile(1, 2, 3);

        try
        {
            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", file.ContentType);
            Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]  // Negative zoom
    [InlineData(0, -1, 0)]  // Negative x
    [InlineData(0, 0, -1)]  // Negative y
    [InlineData(23, 0, 0)]  // Zoom exceeds max (22)
    [InlineData(99, 5, 5)]  // Way out of range zoom
    public async Task GetTile_BadRequest_WhenCoordinatesInvalid(int z, int x, int y)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        var controller = BuildController(cacheDir: cacheDir);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        var result = await controller.GetTile(z, x, y);

        try
        {
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Invalid tile coordinates.", badRequest.Value);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]   // Valid min
    [InlineData(22, 100, 100)]  // Valid max zoom
    [InlineData(10, 512, 384)]  // Typical tile request
    public async Task GetTile_AcceptsValidCoordinates(int z, int x, int y)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        var controller = BuildController(cacheDir: cacheDir);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        var result = await controller.GetTile(z, x, y);

        try
        {
            // Should not be a BadRequest - will be NotFound since tile doesn't exist, but that's fine
            Assert.IsNotType<BadRequestObjectResult>(result);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public async Task GetTile_RateLimitExceeded_Returns429()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        // Set a very low rate limit to trigger it easily
        var settingsService = BuildSettingsService(rateLimitEnabled: true, rateLimitPerMinute: 2);
        var controller = BuildController(cacheDir: cacheDir, settingsService: settingsService);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        // Use a unique IP for this test to avoid interference from other tests
        var uniqueIp = $"192.168.99.{new Random().Next(1, 255)}";
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(uniqueIp);

        try
        {
            // First two requests should succeed (rate limit is 2)
            var result1 = await controller.GetTile(1, 1, 1);
            Assert.IsNotType<ObjectResult>(result1);

            var result2 = await controller.GetTile(1, 1, 2);
            Assert.IsNotType<ObjectResult>(result2);

            // Third request should be rate limited
            var result3 = await controller.GetTile(1, 1, 3);
            var statusResult = Assert.IsType<ObjectResult>(result3);
            Assert.Equal(429, statusResult.StatusCode);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public async Task GetTile_RateLimitDisabled_NoLimit()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        // Disable rate limiting
        var settingsService = BuildSettingsService(rateLimitEnabled: false, rateLimitPerMinute: 1);
        var controller = BuildController(cacheDir: cacheDir, settingsService: settingsService);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        // Use a unique IP for this test
        var uniqueIp = $"192.168.88.{new Random().Next(1, 255)}";
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(uniqueIp);

        try
        {
            // Even with rate limit of 1, should not be limited because it's disabled
            var result1 = await controller.GetTile(1, 1, 1);
            var result2 = await controller.GetTile(1, 1, 2);
            var result3 = await controller.GetTile(1, 1, 3);

            // None should be 429
            Assert.IsNotType<ObjectResult>(result1);
            Assert.IsNotType<ObjectResult>(result2);
            Assert.IsNotType<ObjectResult>(result3);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public async Task GetTile_RespectXForwardedFor()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        // Set a very low rate limit
        var settingsService = BuildSettingsService(rateLimitEnabled: true, rateLimitPerMinute: 1);
        var controller = BuildController(cacheDir: cacheDir, settingsService: settingsService);
        controller.ControllerContext.HttpContext.Request.Headers["Referer"] = "http://example.com/page";

        // Use unique IPs for this test
        var proxyIp = $"10.0.0.{new Random().Next(1, 255)}";
        var clientIp1 = $"203.0.113.{new Random().Next(1, 127)}";
        var clientIp2 = $"203.0.113.{new Random().Next(128, 255)}";

        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(proxyIp);

        try
        {
            // First request from client 1
            controller.ControllerContext.HttpContext.Request.Headers["X-Forwarded-For"] = clientIp1;
            var result1 = await controller.GetTile(1, 1, 1);

            // Second request from client 1 should be rate limited
            var result2 = await controller.GetTile(1, 1, 2);
            var statusResult = Assert.IsType<ObjectResult>(result2);
            Assert.Equal(429, statusResult.StatusCode);

            // Request from different client IP should succeed
            controller.ControllerContext.HttpContext.Request.Headers["X-Forwarded-For"] = clientIp2;
            var result3 = await controller.GetTile(1, 1, 3);
            Assert.IsNotType<ObjectResult>(result3);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    private TilesController BuildController(TileCacheService? tileService = null, ApplicationDbContext? dbContext = null!, string? cacheDir = null, HttpMessageHandler? handler = null, IApplicationSettingsService? settingsService = null)
    {
        dbContext ??= CreateDbContext();
        cacheDir ??= Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        handler ??= new FakeHttpMessageHandler(HttpStatusCode.NotFound);
        settingsService ??= BuildSettingsService();
        var controller = new TilesController(
            NullLogger<TilesController>.Instance,
            tileService ?? CreateTileService(dbContext, handler, cacheDir, settingsService),
            settingsService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Request.Host = new HostString("example.com");
        return controller;
    }

    private TileCacheService CreateTileService(ApplicationDbContext dbContext, HttpMessageHandler handler, string cacheDir, IApplicationSettingsService settingsService)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = cacheDir
            })
            .Build();

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://example.com")
        };

        return new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            httpClient,
            dbContext,
            settingsService,
            Mock.Of<IServiceScopeFactory>());
    }

    private IApplicationSettingsService BuildSettingsService(bool rateLimitEnabled = true, int rateLimitPerMinute = 500)
    {
        // Use a consistent settings instance for controller + cache service tests.
        var appSettings = new Mock<IApplicationSettingsService>();
        appSettings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            MaxCacheTileSizeInMB = 128,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution,
            TileRateLimitEnabled = rateLimitEnabled,
            TileRateLimitPerMinute = rateLimitPerMinute
        });
        return appSettings.Object;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _payload;

        public FakeHttpMessageHandler(HttpStatusCode statusCode, byte[]? payload = null)
        {
            _statusCode = statusCode;
            _payload = payload ?? Array.Empty<byte>();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_payload)
            };
            return Task.FromResult(response);
        }
    }
}
