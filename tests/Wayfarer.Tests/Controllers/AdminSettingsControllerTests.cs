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
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
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
            Mock.Of<IServiceScopeFactory>(),
            new HttpContextAccessor(),
            new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance));

        var scopeFactory = BuildScopeFactory(tileCache);
        var controller = new SettingsController(NullLogger<BaseController>.Instance, db, settingsMock.Object, tileCache, Mock.Of<IProxiedImageCacheService>(), env.Object, scopeFactory, new SseService());
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
            MaxCacheTileSizeInMB = 512,
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
            MaxCacheTileSizeInMB = 1024,
            UploadSizeLimitMB = 100
        };

        var result = await controller.Update(updatedSettings);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var updated = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(updated);
        Assert.True(updated.IsRegistrationOpen);
        Assert.Equal(15, updated.LocationTimeThresholdMinutes);
        Assert.Equal(1024, updated.MaxCacheTileSizeInMB);
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
    public async Task Historical30Actions_ChangeOnlyBudgetOrAcknowledgement()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            TileOutboundBudgetPerIpPerMinute = 30
        });
        await db.SaveChangesAsync();
        var (controller, settingsMock, _) = BuildController(db);

        await controller.AcknowledgeHistoricalTileOutboundBudget();
        var acknowledged = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(acknowledged);
        Assert.Equal(30, acknowledged.TileOutboundBudgetPerIpPerMinute);
        Assert.True(acknowledged.TileOutboundBudgetHistorical30Acknowledged);
        Assert.True(acknowledged.IsRegistrationOpen);

        acknowledged.TileOutboundBudgetHistorical30Acknowledged = false;
        await db.SaveChangesAsync();
        await controller.UseRecommendedTileOutboundBudget();
        var recommended = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(recommended);
        Assert.Equal(80, recommended.TileOutboundBudgetPerIpPerMinute);
        Assert.False(recommended.TileOutboundBudgetHistorical30Acknowledged);
        Assert.True(recommended.IsRegistrationOpen);
        settingsMock.Verify(service => service.RefreshSettings(), Times.Exactly(2));
    }

    /// <summary>Applying the recommendation records only its single budget transition.</summary>
    [Fact]
    public async Task UseRecommendedTileOutboundBudget_RecordsOneFocusedAuditTransition()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            TileOutboundBudgetPerIpPerMinute = 30
        });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        await controller.UseRecommendedTileOutboundBudget();

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("TileOutboundBudgetRecommendedApplied", audit.Action);
        Assert.Equal("admin", audit.UserId);
        Assert.Contains("TileOutboundBudgetPerIpPerMinute: 30 -> 80", audit.Details);
    }

    /// <summary>Acknowledging the historical value records only its single acknowledgement transition.</summary>
    [Fact]
    public async Task AcknowledgeHistoricalTileOutboundBudget_RecordsOneFocusedAuditTransition()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            TileOutboundBudgetPerIpPerMinute = 30,
            TileOutboundBudgetHistorical30Acknowledged = false
        });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        await controller.AcknowledgeHistoricalTileOutboundBudget();

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("TileOutboundBudgetHistorical30Acknowledged", audit.Action);
        Assert.Equal("admin", audit.UserId);
        Assert.Contains("TileOutboundBudgetHistorical30Acknowledged: False -> True", audit.Details);
    }

    [Fact]
    public async Task Update_RejectsInvalidCustomProviderCrossFieldLimits()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        var result = await controller.Update(new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = TileProviderCatalog.CustomProviderKey,
            TileTrafficMode = TileTrafficMode.Custom,
            TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png",
            TileProviderAttribution = "Example",
            TileProviderAdvancedLimitsEnabled = true,
            TileProviderBurstCapacity = 2,
            TileProviderMaxConcurrency = 6,
            TileProviderFallbackBaseDelayMs = 5000,
            TileProviderFallbackDelayCapSeconds = 1,
            TileProviderMaxIndividualWaitSeconds = 120,
            TileProviderTotalRetryCeilingSeconds = 5
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Update_ReappliesSelectedPresetAttribution()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);
        var updatedSettings = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = "https://malicious.example/{z}/{x}/{y}.png",
            TileProviderAttribution = "<script>wrong()</script>Wrong provider"
        };

        var result = await controller.Update(updatedSettings);

        Assert.IsType<RedirectToActionResult>(result);
        var stored = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(stored);
        Assert.Equal(ApplicationSettings.DefaultTileProviderUrlTemplate, stored.TileProviderUrlTemplate);
        Assert.Contains("OpenStreetMap contributors", stored.TileProviderAttribution);
        Assert.DoesNotContain("Wrong provider", stored.TileProviderAttribution);
    }

    /// <summary>Unsupported persisted provider state survives an unrelated normal form save.</summary>
    [Theory]
    [InlineData("thunderforest-cycle", "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png")]
    [InlineData("carto-dark", "https://cartodb-basemaps-a.global.ssl.fastly.net/dark_all/{z}/{x}/{y}.png")]
    [InlineData("retired-provider", "https://tiles.retired.example/{z}/{x}/{y}.png")]
    [InlineData("", "")]
    [InlineData("custom", "malformed endpoint")]
    public async Task Update_UnrelatedSavePreservesUnsupportedProviderRecoveryState(
        string providerKey, string template)
    {
        const string attribution = "Recovery attribution";
        const string apiKey = "recovery-secret";
        var db = CreateDbContext();
        var existing = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = providerKey,
            TileProviderUrlTemplate = template,
            TileProviderAttribution = attribution,
            TileProviderApiKey = apiKey,
            TileProviderSustainedRequestsPerSecond = 17,
            TileProviderBurstCapacity = 41,
            TileProviderMaxConcurrency = 9,
            TileOutboundBudgetPerIpPerMinute = 321,
            TileProviderMaxAttempts = 2,
            TileProviderFallbackBaseDelayMs = 750,
            TileProviderFallbackDelayCapSeconds = 7,
            TileProviderMaxIndividualWaitSeconds = 44,
            TileProviderTotalRetryCeilingSeconds = 88,
            IsRegistrationOpen = false
        };
        db.ApplicationSettings.Add(existing);
        await db.SaveChangesAsync();
        var originalIdentity = TileProviderCatalog.CreateCacheIdentity(providerKey, template);
        var (controller, _, _) = BuildController(db);

        var result = await controller.Update(new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            TileProviderKey = providerKey
        });

        Assert.IsType<RedirectToActionResult>(result);
        var stored = Assert.IsType<ApplicationSettings>(await db.ApplicationSettings.FindAsync(1));
        Assert.Equal(providerKey, stored.TileProviderKey);
        Assert.Equal(template, stored.TileProviderUrlTemplate);
        Assert.Equal(attribution, stored.TileProviderAttribution);
        Assert.Equal(apiKey, stored.TileProviderApiKey);
        Assert.Equal(17, stored.TileProviderSustainedRequestsPerSecond);
        Assert.Equal(41, stored.TileProviderBurstCapacity);
        Assert.Equal(9, stored.TileProviderMaxConcurrency);
        Assert.Equal(321, stored.TileOutboundBudgetPerIpPerMinute);
        Assert.Equal(2, stored.TileProviderMaxAttempts);
        Assert.Equal(750, stored.TileProviderFallbackBaseDelayMs);
        Assert.Equal(7, stored.TileProviderFallbackDelayCapSeconds);
        Assert.Equal(44, stored.TileProviderMaxIndividualWaitSeconds);
        Assert.Equal(88, stored.TileProviderTotalRetryCeilingSeconds);
        Assert.Equal(originalIdentity, TileProviderCatalog.CreateCacheIdentity(
            stored.TileProviderKey, stored.TileProviderUrlTemplate));
    }

    /// <summary>A successful policy save emits one complete bounded non-secret transition.</summary>
    [Fact]
    public async Task Update_TrafficModeAuditsCompleteEffectivePolicyOnce()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);
        var updated = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution,
            TileTrafficMode = TileTrafficMode.Conservative
        };

        await controller.Update(updated);

        var audit = Assert.Single(db.AuditLogs, entry => entry.Action == "SettingsUpdate");
        Assert.Contains("TileEffectiveRateActive", audit.Details);
        Assert.Contains("TileEffectiveBurstActive", audit.Details);
        Assert.Contains("TileEffectiveConcurrencyActive", audit.Details);
        Assert.Contains("TileEffectiveClientAllowanceActive", audit.Details);
        Assert.Contains("TileEffectiveMaxAttempts", audit.Details);
        Assert.Contains("TileEffectiveFallbackBaseDelayMs", audit.Details);
        Assert.Contains("TileEffectiveFallbackDelayCapSeconds", audit.Details);
        Assert.Contains("TileEffectiveMaxIndividualWaitSeconds", audit.Details);
        Assert.Contains("TileEffectiveTotalRetryCeilingSeconds", audit.Details);
        Assert.DoesNotContain(updated.TileProviderUrlTemplate, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PreservesAndSanitizesCustomProviderAttribution()
    {
        const string customTemplate = "https://tiles.example.com/{z}/{x}/{y}.png";
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = "custom",
            TileProviderUrlTemplate = customTemplate,
            TileProviderAttribution = "Example Maps"
        });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);
        var updatedSettings = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = "custom",
            TileProviderUrlTemplate = customTemplate,
            TileProviderAttribution =
                "<script>alert(1)</script>&copy; <a href=\"https://tiles.example.com/terms\" onclick=\"evil()\">Example Maps</a>"
        };

        var result = await controller.Update(updatedSettings);

        Assert.IsType<RedirectToActionResult>(result);
        var stored = await db.ApplicationSettings.FindAsync(1);
        Assert.NotNull(stored);
        Assert.Contains("https://tiles.example.com/terms", stored.TileProviderAttribution);
        Assert.Contains("Example Maps", stored.TileProviderAttribution);
        Assert.DoesNotContain("script", stored.TileProviderAttribution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", stored.TileProviderAttribution, StringComparison.OrdinalIgnoreCase);
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
    public async Task Update_ReturnsView_WhenTileMetadataHotCacheSizeIsOutOfRange()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();

        var (controller, settingsMock, _) = BuildController(db);

        var result = await controller.Update(new ApplicationSettings
        {
            Id = 1,
            TileMetadataHotCacheSizeMB = 8
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
        Assert.False(controller.ModelState.IsValid);
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
            Mock.Of<IServiceScopeFactory>(),
            new HttpContextAccessor(),
            new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance));

        var scopeFactory = BuildScopeFactory(tileCache);
        var controller = new SettingsController(
            NullLogger<BaseController>.Instance,
            db,
            settingsService ?? settingsMock!.Object,
            tileCache,
            Mock.Of<IProxiedImageCacheService>(),
            env.Object,
            scopeFactory,
            new SseService());

        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        return (controller, settingsMock!, tileCache);
    }

    private IServiceScopeFactory BuildScopeFactory(TileCacheService tileCache)
    {
        var services = new ServiceCollection()
            .AddSingleton(tileCache)
            .BuildServiceProvider();
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(services);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(s => s.CreateScope()).Returns(scope.Object);
        return scopeFactory.Object;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
