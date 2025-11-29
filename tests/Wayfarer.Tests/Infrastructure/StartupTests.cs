using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Smoke tests that verify the application can start and build its DI container.
/// These tests are critical for catching configuration issues during framework upgrades.
/// </summary>
public class StartupTests
{
    /// <summary>
    /// Verifies that all core services can be resolved from the DI container.
    /// This catches missing registrations, circular dependencies, and configuration errors.
    /// </summary>
    [Fact]
    public void DependencyInjection_CanResolveAllCoreServices()
    {
        // Arrange - Build a minimal service collection mimicking Program.cs
        var services = new ServiceCollection();

        // Add configuration
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;",
                ["Logging:LogFilePath:Default"] = "logs/test.log",
                ["MobileSse:Enabled"] = "false"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        // Add EF Core with in-memory database (avoids PostgreSQL dependency)
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase("StartupTest"));

        // Add Identity
        services.AddIdentity<ApplicationUser, IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        // Add core services (same as ConfigureServices in Program.cs)
        services.AddMemoryCache();
        services.AddLogging();
        services.AddHttpContextAccessor();

        // Application services
        services.AddScoped<IApplicationSettingsService, ApplicationSettingsService>();
        services.AddScoped<ApiTokenService>();
        services.AddTransient<IRegistrationService, RegistrationService>();
        services.AddSingleton<LocationDataParserFactory>(sp =>
            new LocationDataParserFactory(NullLoggerFactory.Instance));
        services.AddHttpClient<ReverseGeocodingService>();
        services.AddScoped<ILocationImportService, LocationImportService>();
        services.AddScoped<LocationService>();
        services.AddSingleton<SseService>();
        services.AddScoped<ILocationStatsService, LocationStatsService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<IGroupTimelineService, GroupTimelineService>();
        services.AddScoped<IMobileCurrentUserAccessor, MobileCurrentUserAccessor>();
        services.AddScoped<ITripTagService, TripTagService>();
        services.AddSingleton<IUserColorService, UserColorService>();

        // Build provider
        var serviceProvider = services.BuildServiceProvider();

        // Act & Assert - Verify each service can be resolved
        using var scope = serviceProvider.CreateScope();
        var provider = scope.ServiceProvider;

        // Core framework services
        Assert.NotNull(provider.GetService<ApplicationDbContext>());
        Assert.NotNull(provider.GetService<UserManager<ApplicationUser>>());
        Assert.NotNull(provider.GetService<RoleManager<IdentityRole>>());

        // Application services
        Assert.NotNull(provider.GetService<IApplicationSettingsService>());
        Assert.NotNull(provider.GetService<ApiTokenService>());
        Assert.NotNull(provider.GetService<IRegistrationService>());
        Assert.NotNull(provider.GetService<LocationDataParserFactory>());
        Assert.NotNull(provider.GetService<ILocationImportService>());
        Assert.NotNull(provider.GetService<LocationService>());
        Assert.NotNull(provider.GetService<SseService>());
        Assert.NotNull(provider.GetService<ILocationStatsService>());
        Assert.NotNull(provider.GetService<IGroupService>());
        Assert.NotNull(provider.GetService<IInvitationService>());
        Assert.NotNull(provider.GetService<IGroupTimelineService>());
        Assert.NotNull(provider.GetService<IMobileCurrentUserAccessor>());
        Assert.NotNull(provider.GetService<ITripTagService>());
        Assert.NotNull(provider.GetService<IUserColorService>());
    }

    /// <summary>
    /// Verifies that the ApplicationDbContext can be created and its model validated.
    /// This catches EF Core model configuration issues after framework upgrades.
    /// </summary>
    [Fact]
    public void DbContext_ModelIsValid()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("ModelValidationTest")
            .Options;

        var services = new ServiceCollection().BuildServiceProvider();

        // Act
        using var context = new ApplicationDbContext(options, services);

        // Assert - This will throw if the model has configuration issues
        var model = context.Model;
        Assert.NotNull(model);

        // Verify key entity types exist in the model
        Assert.NotNull(model.FindEntityType(typeof(ApplicationUser)));
        Assert.NotNull(model.FindEntityType(typeof(Location)));
        Assert.NotNull(model.FindEntityType(typeof(Trip)));
        Assert.NotNull(model.FindEntityType(typeof(Group)));
    }

    /// <summary>
    /// Verifies that parser factory can create all supported parsers.
    /// This catches issues with parser registrations after updates.
    /// </summary>
    [Fact]
    public void ParserFactory_CanCreateAllParsers()
    {
        // Arrange
        var factory = new LocationDataParserFactory(NullLoggerFactory.Instance);

        // Act & Assert - Verify each supported format (matches LocationDataParserFactory switch)
        Assert.NotNull(factory.GetParser(LocationImportFileType.Csv));
        Assert.NotNull(factory.GetParser(LocationImportFileType.Gpx));
        Assert.NotNull(factory.GetParser(LocationImportFileType.Kml));
        Assert.NotNull(factory.GetParser(LocationImportFileType.GoogleTimeline));
        Assert.NotNull(factory.GetParser(LocationImportFileType.WayfarerGeoJson));
    }
}
