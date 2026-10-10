using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API settings exposure.
/// </summary>
public class ApiSettingsControllerTests : TestBase
{
    /// <summary>Loads persisted default/configured thresholds through the real service with an isolated cache.</summary>
    [Theory]
    [InlineData(false, 5, 15, 50)]
    [InlineData(true, 7, 25, 75)]
    public void GetSettings_ReturnsPersistedThresholds_WithRealService(
        bool configured, int expectedTime, int expectedDistance, int expectedAccuracy)
    {
        using var db = CreateDbContext();
        var settings = new ApplicationSettings { Id = 1 };
        if (configured)
        {
            settings.LocationTimeThresholdMinutes = 7;
            settings.LocationDistanceThresholdMeters = 25;
            settings.LocationAccuracyThresholdMeters = 75;
        }
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();
        // Force the service to read the persisted row rather than the tracked seed.
        db.ChangeTracker.Clear();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var settingsSvc = new ApplicationSettingsService(db, cache);
        var controller = new SettingsController(db, NullLogger<BaseApiController>.Instance, settingsSvc);

        var result = controller.GetSettings();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ApiSettingsDto>(ok.Value);
        Assert.Equal(expectedTime, dto.LocationTimeThresholdMinutes);
        Assert.Equal(expectedDistance, dto.LocationDistanceThresholdMeters);
        Assert.Equal(expectedAccuracy, dto.LocationAccuracyThresholdMeters);
    }
}
