using System.Collections.Generic;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin settings controller basics.
/// </summary>
public class AdminSettingsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsView_WithSettings()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, MaxCacheTileSizeInMB = 10, UploadSizeLimitMB = 5 });
        db.SaveChanges();

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings { Id = 1, MaxCacheTileSizeInMB = 10, UploadSizeLimitMB = 5 });
        settingsMock.Setup(s => s.GetUploadsDirectoryPath()).Returns(Path.Combine(Path.GetTempPath(), "uploads"));

        var tileCacheDir = Path.Combine(Path.GetTempPath(), "tile-cache");
        Directory.CreateDirectory(tileCacheDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = tileCacheDir
            })
            .Build();

        var tileCache = new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            new HttpClient(new FakeHandler()),
            db,
            settingsMock.Object,
            Mock.Of<IServiceScopeFactory>());

        var controller = new SettingsController(NullLogger<BaseController>.Instance, db, settingsMock.Object, tileCache, env.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("admin", "Admin") };

        var result = await controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
