using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="RegistrationService"/> covering registration status checks.
/// </summary>
public class RegistrationServiceTests : TestBase
{
    #region CheckRegistration Tests

    [Fact]
    public void CheckRegistration_DoesNotRedirect_WhenNoSettingsExist()
    {
        // Arrange
        var db = CreateDbContext();
        var mockConfig = new Mock<IConfiguration>();
        var httpContext = new DefaultHttpContext();

        var service = new RegistrationService(db, mockConfig.Object);

        // Act
        service.CheckRegistration(httpContext);

        // Assert - No redirect, response should not be redirected
        Assert.False(httpContext.Response.HasStarted);
        Assert.Null(httpContext.Response.Headers["Location"].FirstOrDefault());
    }

    [Fact]
    public void CheckRegistration_DoesNotRedirect_WhenRegistrationIsOpen()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            IsRegistrationOpen = true
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var mockConfig = new Mock<IConfiguration>();
        var httpContext = new DefaultHttpContext();

        var service = new RegistrationService(db, mockConfig.Object);

        // Act
        service.CheckRegistration(httpContext);

        // Assert - No redirect
        Assert.Null(httpContext.Response.Headers["Location"].FirstOrDefault());
    }

    [Fact]
    public void CheckRegistration_Redirects_WhenRegistrationIsClosed()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            IsRegistrationOpen = false
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var mockConfig = new Mock<IConfiguration>();
        var httpContext = new DefaultHttpContext();

        var service = new RegistrationService(db, mockConfig.Object);

        // Act
        service.CheckRegistration(httpContext);

        // Assert - Should redirect to registration closed page
        Assert.Equal("/Home/RegistrationClosed", httpContext.Response.Headers["Location"].FirstOrDefault());
    }

    [Fact]
    public void CheckRegistration_RedirectsToCorrectPath()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            IsRegistrationOpen = false
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var mockConfig = new Mock<IConfiguration>();
        var httpContext = new DefaultHttpContext();

        var service = new RegistrationService(db, mockConfig.Object);

        // Act
        service.CheckRegistration(httpContext);

        // Assert - Verify exact redirect path
        var location = httpContext.Response.Headers["Location"].FirstOrDefault();
        Assert.NotNull(location);
        Assert.StartsWith("/Home/RegistrationClosed", location);
    }

    [Fact]
    public void CheckRegistration_UsesFirstSettings_WhenMultipleExist()
    {
        // Arrange
        var db = CreateDbContext();
        var settings1 = new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = false
        };
        var settings2 = new ApplicationSettings
        {
            Id = 2,
            IsRegistrationOpen = true
        };
        db.ApplicationSettings.Add(settings1);
        db.ApplicationSettings.Add(settings2);
        db.SaveChanges();

        var mockConfig = new Mock<IConfiguration>();
        var httpContext = new DefaultHttpContext();

        var service = new RegistrationService(db, mockConfig.Object);

        // Act
        service.CheckRegistration(httpContext);

        // Assert - Should use first settings (registration closed)
        Assert.NotNull(httpContext.Response.Headers["Location"].FirstOrDefault());
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Arrange
        var db = CreateDbContext();
        var mockConfig = new Mock<IConfiguration>();

        // Act
        var service = new RegistrationService(db, mockConfig.Object);

        // Assert - Service should be created without errors
        Assert.NotNull(service);
    }

    #endregion
}
