using System.Collections.Generic;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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

    [Fact]
    public async Task Update_ReturnsView_WhenModelStateInvalid()
    {
        var (controller, _, _) = BuildController();
        controller.ModelState.AddModelError("LocationTimeThresholdMinutes", "required");

        var result = await controller.Update(new ApplicationSettings { Id = 1 });

        // Missing settings should return the index view with validation errors.
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
    }

    [Fact]
    public async Task Update_UpdatesSettings_WhenValid()
    {
        var db = CreateDbContext();
        var existingSettings = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = false,
            LocationTimeThresholdMinutes = 10,
            MaxCacheTileSizeInMB = 100,
            UploadSizeLimitMB = 50
        };
        db.ApplicationSettings.Add(existingSettings);
        await db.SaveChangesAsync();

        var (controller, settingsMock, _) = BuildController(db);

        var updatedSettings = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            LocationTimeThresholdMinutes = 15,
            MaxCacheTileSizeInMB = 200,
            UploadSizeLimitMB = 100
        };

        var result = await controller.Update(updatedSettings);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var updated = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(updated);
        Assert.True(updated.IsRegistrationOpen);
        Assert.Equal(15, updated.LocationTimeThresholdMinutes);
        Assert.Equal(200, updated.MaxCacheTileSizeInMB);
        Assert.Equal(100, updated.UploadSizeLimitMB);

        settingsMock.Verify(s => s.RefreshSettings(), Times.Once);
    }

    [Fact]
    public async Task Update_CallsRefreshSettings_AfterUpdate()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();

        var (controller, settingsMock, _) = BuildController(db);

        await controller.Update(new ApplicationSettings { Id = 1, IsRegistrationOpen = true });

        settingsMock.Verify(s => s.RefreshSettings(), Times.Once);
    }

    [Fact]
    public async Task Update_DoesNotUpdate_WhenSettingsNotFound()
    {
        var db = CreateDbContext();
        var (controller, settingsMock, _) = BuildController(db);

        var result = await controller.Update(new ApplicationSettings { Id = 1 });

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
        settingsMock.Verify(s => s.RefreshSettings(), Times.Never);
    }

    [Fact]
    public async Task Update_TracksChanges_InAuditLog()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(user);
        var existingSettings = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = false,
            LocationTimeThresholdMinutes = 10
        };
        db.ApplicationSettings.Add(existingSettings);
        await db.SaveChangesAsync();

        var (controller, _, _) = BuildController(db);

        var updatedSettings = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            LocationTimeThresholdMinutes = 15
        };

        await controller.Update(updatedSettings);

        var auditLog = db.AuditLogs.FirstOrDefault(a => a.Action == "SettingsUpdate");
        Assert.NotNull(auditLog);
        Assert.Contains("IsRegistrationOpen", auditLog.Details);
        Assert.Contains("LocationTimeThresholdMinutes", auditLog.Details);
    }

    [Fact]
    public void ClearMbtilesCache_RedirectsToIndex()
    {
        var (controller, _, _) = BuildController();

        var result = controller.ClearMbtilesCache();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.NotNull(controller.TempData["Message"]);
    }

    private (SettingsController controller, Mock<IApplicationSettingsService> settingsMock, TileCacheService tileCache)
        BuildController(ApplicationDbContext? db = null, IApplicationSettingsService? settingsService = null)
    {
        db ??= CreateDbContext();

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var settingsMock = settingsService == null ? new Mock<IApplicationSettingsService>() : null;
        if (settingsMock != null)
        {
            settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        }

        var tileCacheDir = Path.Combine(Path.GetTempPath(), $"tilecache-{Guid.NewGuid():N}");
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
            settingsService ?? settingsMock!.Object,
            Mock.Of<IServiceScopeFactory>());

        var controller = new SettingsController(
            NullLogger<BaseController>.Instance,
            db,
            settingsService ?? settingsMock!.Object,
            tileCache,
            env.Object);

        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        return (controller, settingsMock!, tileCache);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
