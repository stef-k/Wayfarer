using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Registration guard behavior based on application settings.
/// </summary>
public class RegistrationServiceTests : TestBase
{
    [Fact]
    public void CheckRegistration_Continues_WhenOpen()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB,
            UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB
        });
        db.SaveChanges();

        var ctx = new DefaultHttpContext();
        var service = new RegistrationService(db, BuildConfig());

        service.CheckRegistration(ctx);

        Assert.False(ctx.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public void CheckRegistration_Redirects_WhenClosed()
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
        var service = new RegistrationService(db, BuildConfig());

        service.CheckRegistration(ctx);

        Assert.Equal("/Home/RegistrationClosed", ctx.Response.Headers["Location"]);
    }

    private static IConfiguration BuildConfig()
    {
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider()
            .GetRequiredService<IConfiguration>();
    }
}
