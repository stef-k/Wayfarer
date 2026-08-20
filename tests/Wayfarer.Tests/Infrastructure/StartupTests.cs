using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Wayfarer.Middleware;
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
    /// Verifies that the Microsoft logging bridge registers one Serilog provider and
    /// preserves structured scopes plus request context on emitted events.
    /// </summary>
    [Fact]
    public async Task SerilogProvider_EmitsStructuredRequestContextWithoutDuplicates()
    {
        var sink = new CollectingSink();
        using var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(serilogLogger, dispose: false);
        });

        using var serviceProvider = services.BuildServiceProvider();
        var providers = serviceProvider.GetServices<ILoggerProvider>();
        var logger = serviceProvider.GetRequiredService<ILogger<StartupTests>>();
        var context = new DefaultHttpContext { TraceIdentifier = "request-462" };
        var middleware = new RequestIdLoggingMiddleware(async _ =>
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["Operation"] = "startup" }))
                logger.LogInformation("Serilog integration {Issue}", 462);
            await Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Single(providers);
        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(462, Assert.IsType<ScalarValue>(logEvent.Properties["Issue"]).Value);
        Assert.Equal("startup", Assert.IsType<ScalarValue>(logEvent.Properties["Operation"]).Value);
        Assert.Equal("request-462", Assert.IsType<ScalarValue>(logEvent.Properties["RequestId"]).Value);
    }

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

    /// <summary>Collects Serilog events without an external sink dependency.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        /// <summary>Gets the events emitted through the Microsoft logging provider.</summary>
        public List<LogEvent> Events { get; } = [];

        /// <inheritdoc />
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
