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

    private IApplicationSettingsService BuildSettingsService()
    {
        // Use a consistent settings instance for controller + cache service tests.
        var appSettings = new Mock<IApplicationSettingsService>();
        appSettings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            MaxCacheTileSizeInMB = 128,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution
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
