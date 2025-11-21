using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Application settings caching and registration guard rails.
/// </summary>
public class ApplicationSettingsServiceTests : TestBase
{
    [Fact]
    public void GetSettings_CachesDefaults()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = 0, // will default
            UploadSizeLimitMB = 0,
            IsRegistrationOpen = true
        });
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);

        var settings1 = service.GetSettings();
        var settings2 = service.GetSettings();

        Assert.Same(settings1, settings2); // cached
        Assert.Equal(ApplicationSettings.DefaultMaxCacheTileSizeInMB, settings1.MaxCacheTileSizeInMB);
        Assert.Equal(ApplicationSettings.DefaultUploadSizeLimitMB, settings1.UploadSizeLimitMB);
    }

    [Fact]
    public void RefreshSettings_ReloadsFromDb()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = 100,
            UploadSizeLimitMB = 10,
            IsRegistrationOpen = true
        });
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ApplicationSettingsService(db, cache);
        service.GetSettings(); // warm cache
        var dbSettings = db.ApplicationSettings.First();
        dbSettings.MaxCacheTileSizeInMB = 150;
        db.SaveChanges();

        service.RefreshSettings();
        var refreshed = service.GetSettings();

        Assert.Equal(150, refreshed.MaxCacheTileSizeInMB); // pulled fresh
    }

    [Fact]
    public void CheckRegistration_RedirectsWhenClosed()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = false,
            MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB,
            UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB
        });
        db.SaveChanges();

        var ctx = new DefaultHttpContext();
        var config = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider().GetRequiredService<IConfiguration>();
        var service = new RegistrationService(db, config);

        service.CheckRegistration(ctx);

        Assert.Equal("/Home/RegistrationClosed", ctx.Response.Headers["Location"]);
    }
}
