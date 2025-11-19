using Microsoft.Extensions.Caching.Memory;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="ApplicationSettingsService"/> covering settings retrieval and caching.
/// </summary>
public class ApplicationSettingsServiceTests : TestBase
{
    #region GetSettings Tests

    [Fact]
    public void GetSettings_ReturnsSettings_WhenExistInDatabase()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 10,
            LocationDistanceThresholdMeters = 20,
            MaxCacheTileSizeInMB = 2048,
            IsRegistrationOpen = true,
            UploadSizeLimitMB = 200
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var result = service.GetSettings();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(10, result.LocationTimeThresholdMinutes);
        Assert.Equal(20, result.LocationDistanceThresholdMeters);
        Assert.Equal(2048, result.MaxCacheTileSizeInMB);
        Assert.True(result.IsRegistrationOpen);
        Assert.Equal(200, result.UploadSizeLimitMB);
    }

    [Fact]
    public void GetSettings_ThrowsException_WhenSettingsNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => service.GetSettings());
        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public void GetSettings_UsesDefaultMaxCacheTileSize_WhenZero()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = 0 // Should default
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var result = service.GetSettings();

        // Assert
        Assert.Equal(ApplicationSettings.DefaultMaxCacheTileSizeInMB, result.MaxCacheTileSizeInMB);
    }

    [Fact]
    public void GetSettings_UsesDefaultUploadSizeLimit_WhenZero()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            UploadSizeLimitMB = 0 // Should default
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var result = service.GetSettings();

        // Assert
        Assert.Equal(ApplicationSettings.DefaultUploadSizeLimitMB, result.UploadSizeLimitMB);
    }

    [Fact]
    public void GetSettings_CachesSettings_OnFirstCall()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 15
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act - First call loads from DB
        var result1 = service.GetSettings();

        // Get same reference from second call to prove it's cached
        var result2 = service.GetSettings();

        // Assert - Should return same object instance (cached)
        Assert.Same(result1, result2);
        Assert.Equal(15, result2.LocationTimeThresholdMinutes);
    }

    [Fact]
    public void GetSettings_PreservesNonZeroValues()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = 512,
            UploadSizeLimitMB = 50
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var result = service.GetSettings();

        // Assert - Non-zero values should not be replaced with defaults
        Assert.Equal(512, result.MaxCacheTileSizeInMB);
        Assert.Equal(50, result.UploadSizeLimitMB);
    }

    #endregion

    #region RefreshSettings Tests

    [Fact]
    public void RefreshSettings_UpdatesCache_WithNewValues()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Load initial settings into cache
        var initial = service.GetSettings();
        Assert.Equal(5, initial.LocationTimeThresholdMinutes);

        // Modify in database
        settings.LocationTimeThresholdMinutes = 25;
        db.SaveChanges();

        // Act - Refresh should update cache
        service.RefreshSettings();
        var refreshed = service.GetSettings();

        // Assert
        Assert.Equal(25, refreshed.LocationTimeThresholdMinutes);
    }

    [Fact]
    public void RefreshSettings_ThrowsException_WhenSettingsNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => service.RefreshSettings());
    }

    [Fact]
    public void RefreshSettings_AppliesDefaults_WhenValuesAreZero()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = 1024,
            UploadSizeLimitMB = 100
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Load initial
        service.GetSettings();

        // Update to zero values
        settings.MaxCacheTileSizeInMB = 0;
        settings.UploadSizeLimitMB = 0;
        db.SaveChanges();

        // Act
        service.RefreshSettings();
        var result = service.GetSettings();

        // Assert - Should apply defaults
        Assert.Equal(ApplicationSettings.DefaultMaxCacheTileSizeInMB, result.MaxCacheTileSizeInMB);
        Assert.Equal(ApplicationSettings.DefaultUploadSizeLimitMB, result.UploadSizeLimitMB);
    }

    #endregion

    #region GetUploadsDirectoryPath Tests

    [Fact]
    public void GetUploadsDirectoryPath_ReturnsValidPath()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var path = service.GetUploadsDirectoryPath();

        // Assert
        Assert.NotNull(path);
        Assert.NotEmpty(path);
        Assert.Contains("Uploads", path);
        Assert.Contains("Temp", path);
    }

    [Fact]
    public void GetUploadsDirectoryPath_ContainsBaseDirectory()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        // Act
        var path = service.GetUploadsDirectoryPath();

        // Assert
        Assert.StartsWith(AppContext.BaseDirectory, path);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Act
        var service = new ApplicationSettingsService(db, cache);

        // Assert
        Assert.NotNull(service);
    }

    #endregion
}
