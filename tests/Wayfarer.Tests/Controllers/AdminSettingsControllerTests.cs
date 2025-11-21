using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
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
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin settings update path coverage.
/// </summary>
public class AdminSettingsControllerTests : TestBase
{
    [Fact]
    public async Task Update_WhenModelValid_PersistsChangesAndRefreshes()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, IsRegistrationOpen = false, UploadSizeLimitMB = 10, MaxCacheTileSizeInMB = 20 });
        await db.SaveChangesAsync();
        var settingsService = new FakeSettingsService(db);
        var tileCache = CreateTileCacheService(db, settingsService);
        var env = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == Path.GetTempPath());

        var controller = BuildController(db, settingsService, tileCache, env);
        var updated = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            UploadSizeLimitMB = 50,
            MaxCacheTileSizeInMB = 75
        };

        var result = await controller.Update(updated);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var saved = await db.ApplicationSettings.FindAsync(1);
        Assert.True(saved!.IsRegistrationOpen);
        Assert.Equal(50, saved.UploadSizeLimitMB);
        Assert.Equal(75, saved.MaxCacheTileSizeInMB);
        Assert.True(settingsService.Refreshed);
    }

    [Fact]
    public async Task Update_InvalidModel_ReturnsIndexView()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, IsRegistrationOpen = true, UploadSizeLimitMB = 10, MaxCacheTileSizeInMB = 20 });
        await db.SaveChangesAsync();
        var settingsService = new FakeSettingsService(db);
        var tileCache = CreateTileCacheService(db, settingsService);
        var env = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == Path.GetTempPath());
        var controller = BuildController(db, settingsService, tileCache, env);
        controller.ModelState.AddModelError("IsRegistrationOpen", "required");

        var result = await controller.Update(new ApplicationSettings());

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
    }

    private static SettingsController BuildController(
        ApplicationDbContext db,
        IApplicationSettingsService settings,
        TileCacheService tileCache,
        IWebHostEnvironment env)
    {
        var controller = new SettingsController(
            NullLogger<BaseController>.Instance,
            db,
            settings,
            tileCache,
            env);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static TileCacheService CreateTileCacheService(ApplicationDbContext db, IApplicationSettingsService settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = Path.Combine(Path.GetTempPath(), "tilecache-tests")
            })
            .Build();
        return new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            new HttpClient(),
            db,
            settings,
            Mock.Of<IServiceScopeFactory>());
    }

    private sealed class FakeSettingsService : IApplicationSettingsService
    {
        private readonly ApplicationDbContext _db;
        public bool Refreshed { get; private set; }

        public FakeSettingsService(ApplicationDbContext db) => _db = db;

        public ApplicationSettings GetSettings() => _db.ApplicationSettings.First(s => s.Id == 1);

        public string GetUploadsDirectoryPath() => Path.Combine(Path.GetTempPath(), "uploads");

        public void RefreshSettings() => Refreshed = true;
    }
}
