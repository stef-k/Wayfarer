using System.Collections.Specialized;
using Microsoft.Extensions.FileProviders;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using MvcFrontendKit.Extensions;
using NetTopologySuite.Geometries;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Options;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Serilog;
using Wayfarer.Jobs;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Swagger;
using Wayfarer.Util;
using IPNetwork = System.Net.IPNetwork;
// for AddQuartz(), AddQuartzHostedService()
// for UseMicrosoftDependencyInjectionJobFactory(), UsePersistentStore(), etc.
// for IJobFactory
// for UseNewtonsoftJsonSerializer()

var builder = WebApplication.CreateBuilder(args);

#region CLI Command Handling

// Handling the "reset-password" command from the CLI
if (args.Length > 0 && args[0] == "reset-password") await HandlePasswordResetCommand(args);

#endregion CLI Command Handling

#region Configuration Setup

// Configuring the application settings, such as JSON configuration files
ConfigureConfiguration(builder);

#endregion Configuration Setup

#region Serilog Logging Setup

// Setting up logging, including Serilog for file, console, and PostgreSQL logging
ConfigureLogging(builder);

#endregion Serilog Logging Setup

#region Database Configuration

// Configuring database connection and Entity Framework setup
ConfigureDatabase(builder);

#endregion Database Configuration

#region Identity Configuration

// Configuring authentication, user roles, and identity management
ConfigureIdentity(builder);

#endregion Identity Configuration

#region Forwarded Headers Configuration

// Simple forwarded headers configuration for nginx reverse proxy
static void ConfigureForwardedHeaders(WebApplicationBuilder builder)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        // Configure headers to forward from nginx
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;

        // Clear defaults for explicit configuration
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Trust nginx running on localhost (your setup)
        options.KnownProxies.Add(IPAddress.Parse("127.0.0.1"));
        options.KnownProxies.Add(IPAddress.IPv6Loopback);

        // For nginx on same machine, trust loopback networks
        options.KnownIPNetworks.Add(new IPNetwork(
            IPAddress.Parse("127.0.0.0"), 8));
        options.KnownIPNetworks.Add(new IPNetwork(
            IPAddress.Parse("::1"), 128));

        // Optional: Trust local network ranges if needed
        if (builder.Environment.IsDevelopment())
        {
            // In development, also trust local networks
            options.KnownIPNetworks.Add(new IPNetwork(
                IPAddress.Parse("192.168.0.0"), 16));
            options.KnownIPNetworks.Add(new IPNetwork(
                IPAddress.Parse("10.0.0.0"), 8));
        }

        // Security settings
        options.ForwardLimit = 1; // Only expect one proxy (nginx)

        // For your wayfarer.stefk.me setup, this is sufficient
        if (!builder.Environment.IsDevelopment())
            options.RequireHeaderSymmetry = false; // Allow flexible header presence
    });
}

#endregion Forwarded Headers Configuration

#region Quartz Configuration

// Configuring Quartz for job scheduling
ConfigureQuartz(builder);

#endregion Quartz Configuration

#region Forwarded Headers Configuration

// NEW: Configure forwarded headers for nginx proxy support
ConfigureForwardedHeaders(builder);

#endregion Forwarded Headers Configuration

#region Configure other services

ConfigureServices(builder);

#endregion Configure other services

var app = builder.Build();

// Check and set if needed for Quartz database setup for job persistence
await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(app.Services);

#region Database Seeding

// Seed the database with roles and the admin user if necessary
await SeedDatabase(app);

#endregion Database Seeding

#region Middleware Setup

// Setting up middleware components, including performance monitoring and error handling
ConfigureAreas(app);
ConfigureMiddleware(app).GetAwaiter().GetResult();

#endregion Middleware Setup

app.Run();

static Task<long> LoadUploadSizeLimitFromDatabaseAsync()
{
    // Your logic to load the size limit from the database
    return Task.FromResult(100L * 1024 * 1024); // example: 100MB
}

#region Methods

// Method to handle the password reset command
static async Task HandlePasswordResetCommand(string[] args)
{
    if (args.Length != 3)
    {
        Console.WriteLine("Usage: reset-password <username> <new-password>");
        return;
    }

    var username = args[1];
    var newPassword = args[2];

    // Rebuild services to handle the password reset
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
            x => x.UseNetTopologySuite()));
    builder.Services.AddDefaultIdentity<ApplicationUser>()
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

    var services = builder.Services.BuildServiceProvider();

    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    builder.Services.AddHttpContextAccessor();
    var user = await userManager.FindByNameAsync(username);
    if (user == null)
    {
        Console.WriteLine($"User '{username}' not found.");
        return;
    }

    var token = await userManager.GeneratePasswordResetTokenAsync(user);
    var result = await userManager.ResetPasswordAsync(user, token, newPassword);

    if (result.Succeeded)
    {
        Console.WriteLine($"Password for user '{username}' has been reset successfully.");
    }
    else
    {
        Console.WriteLine("Failed to reset password. Errors:");
        foreach (var error in result.Errors) Console.WriteLine($" - {error.Description}");
    }
}

// Method to configure the application�s configuration settings
static void ConfigureConfiguration(WebApplicationBuilder builder)
{
    // Adding JSON configuration files to the app's configuration pipeline
    // Environment variables are added last to ensure they override JSON settings (e.g., connection strings from systemd)
    builder.Configuration.AddJsonFile("appsettings.json", false, true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true, true)
        .AddEnvironmentVariables();

    // Retrieving the log file path from the configuration
    var logFilePath = builder.Configuration["Logging:LogFilePath:Default"];

    if (string.IsNullOrEmpty(logFilePath))
        throw new InvalidOperationException(
            "Log file path is not configured. Please check your appsettings.json or appsettings.Development.json.");

    // Ensuring that the directory for logs exists
    var logDirectory = Path.GetDirectoryName(logFilePath);
    if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create log directory: {ex.Message}");
            throw;
        }
}

// Method to configure logging with Serilog
static void ConfigureLogging(WebApplicationBuilder builder)
{
    // Retrieve the log file path from configuration
    var logFilePath = builder.Configuration["Logging:LogFilePath:Default"];

    if (string.IsNullOrEmpty(logFilePath))
        throw new InvalidOperationException(
            "Log file path is not configured. Please check your appsettings.json or appsettings.Development.json.");

    // Configure Serilog for logging to console, file, and PostgreSQL
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console() // Logs to the console
        .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day) // Logs to a file with daily rotation
        .WriteTo.PostgreSQL(builder.Configuration.GetConnectionString("DefaultConnection"),
            "AuditLogs", // Table for storing logs
            needAutoCreateTable: true) // Auto-creates the table if it doesn't exist
        .CreateLogger();

    // Add Serilog as the logging provider
    builder.Services.AddLogging(logging =>
    {
        logging.ClearProviders(); // Clears default logging providers
        logging.AddSerilog(); // Adds Serilog as the logging provider
    });
}

// Method to configure the database connection and Entity Framework setup
static void ConfigureDatabase(WebApplicationBuilder builder)
{
    // Retrieve the connection string from the configuration
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
                           throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

    // Add DbContext to the DI container, configure it with PostgreSQL and NetTopologySuite for spatial data
    // builder.Services.AddDbContext<ApplicationDbContext>(options =>
    //     options.UseNpgsql(connectionString, x => x.UseNetTopologySuite()));

    // use a pool of db connections instead of spawning a new per request
    builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
    {
        options.UseNpgsql(connectionString, x => x.UseNetTopologySuite());
        // Suppress pending model changes warning - EF Core sometimes detects false positives
        // that don't result in actual schema changes (e.g., minor snapshot differences)
        options.ConfigureWarnings(warnings =>
            warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    });

    // Add exception handling for database-related errors during development
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();
}

// Method to configure identity, authentication, and user roles
static void ConfigureIdentity(WebApplicationBuilder builder)
{
    // Add default identity services for user authentication
    builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false; // Disables confirmed email requirement
            options.User.RequireUniqueEmail = false; // Allows non-unique email addresses

            // Account lockout settings to protect against brute-force attacks
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
        })
        .AddRoles<IdentityRole>() // Adds role-based authorization
        .AddEntityFrameworkStores<ApplicationDbContext>() // Uses EF Core for user store
        .AddDefaultTokenProviders(); // Adds support for token-based authentication
}

// Method to configure Quartz for job scheduling
static void ConfigureQuartz(WebApplicationBuilder builder)
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

    // 0) Register your job implementations in DI
    //    So JobFactory can resolve them by type
    builder.Services.AddTransient<LogCleanupJob>();
    builder.Services.AddTransient<AuditLogCleanupJob>();
    builder.Services.AddTransient<VisitCleanupJob>();
    // ...and any other IJob implementations you'll use

    // 1) Register your JobFactory & Listeners
    // builder.Services.AddSingleton<IJobFactory, JobFactory>();
    builder.Services.AddSingleton<IJobFactory, ScopedJobFactory>();
    builder.Services.AddScoped<IJobListener, JobExecutionListener>();
    builder.Services.AddTransient<LocationImportJob>();

    // 2) Build & start the Quartz scheduler
    builder.Services.AddSingleton<IScheduler>(sp =>
    {
        var props = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "QuartzScheduler",
            ["quartz.scheduler.instanceId"] = "AUTO",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
            ["quartz.jobStore.tablePrefix"] = "qrtz_",
            ["quartz.jobStore.useProperties"] = "true",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.dataSource.default.provider"] = "Npgsql",
            ["quartz.dataSource.default.connectionString"] = cs,
            ["quartz.serializer.type"] = "Quartz.Simpl.JsonObjectSerializer, Quartz.Serialization.Json"
        };

        var factory = new StdSchedulerFactory(props);
        var scheduler = factory.GetScheduler().Result;

        scheduler.JobFactory = sp.GetRequiredService<IJobFactory>();
        using var scope = sp.CreateScope();
        scheduler.ListenerManager.AddJobListener(scope.ServiceProvider.GetRequiredService<IJobListener>());

        scheduler.Start().Wait();

        // Schedule maintenance jobs once if missing
        var logJobKey = new JobKey("LogCleanupJob", "Maintenance");
        var auditJobKey = new JobKey("AuditLogCleanupJob", "Maintenance");

        if (!scheduler.CheckExists(logJobKey).Result)
        {
            var job = JobBuilder.Create<LogCleanupJob>()
                .WithIdentity(logJobKey)
                .StoreDurably()
                .Build();

            var trigger = TriggerBuilder.Create()
                .ForJob(job)
                .WithIdentity("LogCleanupTrigger", "Maintenance")
                .StartNow()
                .WithSimpleSchedule(x => x.WithIntervalInHours(24).RepeatForever())
                .Build();

            scheduler.ScheduleJob(job, trigger).Wait();
        }

        if (!scheduler.CheckExists(auditJobKey).Result)
        {
            var job = JobBuilder.Create<AuditLogCleanupJob>()
                .WithIdentity(auditJobKey)
                .StoreDurably()
                .Build();

            var trigger = TriggerBuilder.Create()
                .ForJob(job)
                .WithIdentity("AuditLogCleanupTrigger", "Maintenance")
                .StartNow()
                .WithSimpleSchedule(x => x.WithIntervalInHours(24).RepeatForever())
                .Build();

            scheduler.ScheduleJob(job, trigger).Wait();
        }

        // Visit cleanup job - closes stale open visits and deletes stale candidates
        var visitJobKey = new JobKey("VisitCleanupJob", "Maintenance");
        if (!scheduler.CheckExists(visitJobKey).Result)
        {
            var job = JobBuilder.Create<VisitCleanupJob>()
                .WithIdentity(visitJobKey)
                .StoreDurably()
                .Build();

            var trigger = TriggerBuilder.Create()
                .ForJob(job)
                .WithIdentity("VisitCleanupTrigger", "Maintenance")
                .StartNow()
                .WithSimpleSchedule(x => x.WithIntervalInHours(24).RepeatForever())
                .Build();

            scheduler.ScheduleJob(job, trigger).Wait();
        }

        return scheduler;
    });

    // 3) Host Quartz as a background service
    builder.Services.AddSingleton<IHostedService, QuartzHostedService>();
}

// Method to configure services for the application
static void ConfigureServices(WebApplicationBuilder builder)
{
    // Register memory cache for application services
    builder.Services.AddMemoryCache();

    // Register application services with DI container
    builder.Services.AddScoped<IApplicationSettingsService, ApplicationSettingsService>();

    // Register ApiTokenService with DI container
    builder.Services.AddScoped<ApiTokenService>();

    // IRegistrationService as a transient or singleton service
    builder.Services.AddTransient<IRegistrationService, RegistrationService>();

    // Import location data parsing service
    builder.Services.AddSingleton<LocationDataParserFactory>();

    // Import Location Data service
    builder.Services.AddScoped<ILocationImportService, LocationImportService>();

    // Add controllers with views for MVC routing & ingore JSON property-name case
    builder.Services
        .AddControllersWithViews()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });

    // Add MvcFrontendKit for frontend bundling
    builder.Services.AddMvcFrontendKit();


    // Add Swagger generation
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Wayfarer API" });

        // Custom Point converter in Swagger
        // Directly configure how 'Point' is represented in Swagger UI
        c.MapType<Point>(() => new OpenApiSchema
        {
            Type = "string",
            Format = "wkt", // Optional: specify Well-Known Text format (WKT)
            Description = "The coordinates in WKT format (Point)",
            Example = new OpenApiString("48.8588443, 2.2943506"), // Example of WKT format
            Nullable = false // Explicitly set 'Nullable' to false
        });


        // Apply the schema filter to hide PostGIS types
        c.DocumentFilter<RemovePostGisSchemasDocumentFilter>();

        // Use a predicate to include only actions within the "Api" area
        c.DocInclusionPredicate((docName, apiDesc) =>
        {
            var actionDescriptor = apiDesc.ActionDescriptor;

            // Check if the action descriptor has the "area" route value set to "Api"
            return actionDescriptor.RouteValues.ContainsKey("area") &&
                   actionDescriptor.RouteValues["area"] == "Api";
        });
    });

    // PostGIS POINT JSON converter for JSON serialization
    builder.Services.AddControllers()
        .AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new PointJsonConverter()); });

    // Reverse geocoding Mapbox service
    builder.Services.AddHttpClient<ReverseGeocodingService>();

    // Tile Cache service
    builder.Services.AddScoped<TileCacheService>();

    // add the Http client to the Tile Cache service (manual redirects handled in service)
    builder.Services.AddHttpClient<TileCacheService>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

    // Location service, handles location results per zoom and bounds levels
    builder.Services.AddScoped<LocationService>();

    // Server Send Events Service setup (SSE) used to broadcast messages to clients
    builder.Services.AddSingleton<SseService>();

    // User location stats service
    builder.Services.AddScoped<ILocationStatsService, LocationStatsService>();

    // Place visit detection service (auto-visited trip places)
    builder.Services.AddScoped<IPlaceVisitDetectionService, PlaceVisitDetectionService>();

    // Visit backfill service (analyze location history to create visits)
    builder.Services.AddScoped<IVisitBackfillService, VisitBackfillService>();

    // Trip export service (PDF, KML, Google MyMaps KML)
    builder.Services.AddScoped<ITripExportService, TripExportService>();

    builder.Services.AddScoped<IRazorViewRenderer, RazorViewRenderer>();
    builder.Services.AddSingleton<MapSnapshotService>();

    // Trip thumbnail services for public trips index
    builder.Services.AddScoped<ITripThumbnailService, TripThumbnailService>();
    builder.Services.AddSingleton<ITripMapThumbnailGenerator, TripMapThumbnailGenerator>();

    // Trip import service
    builder.Services.AddScoped<ITripImportService, TripImportService>();

    // Groups and invitations
    builder.Services.AddScoped<IGroupService, GroupService>();
    builder.Services.AddScoped<IInvitationService, InvitationService>();
    builder.Services.AddScoped<IGroupTimelineService, GroupTimelineService>();
    builder.Services.AddScoped<IMobileCurrentUserAccessor, MobileCurrentUserAccessor>();
    builder.Services.AddScoped<ITripTagService, TripTagService>();
    builder.Services.AddSingleton<IUserColorService, UserColorService>();
    builder.Services.Configure<MobileSseOptions>(builder.Configuration.GetSection("MobileSse"));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MobileSseOptions>>().Value);
}

// Method to configure middleware components such as error handling and performance monitoring
static async Task ConfigureMiddleware(WebApplication app)
{
    // CRITICAL: Add this as the FIRST middleware to process forwarded headers from nginx
    app.UseForwardedHeaders();

    app.UseMiddleware<PerformanceMonitoringMiddleware>(); // Custom middleware for monitoring performance

    // Use specific middlewares based on the environment
    if (app.Environment.IsDevelopment())
    {
        // Enable Swagger and Swagger UI in development environment
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Wayfarer API v1");
            c.RoutePrefix = "swagger"; // Access Swagger UI at /swagger
        });

        app.UseMigrationsEndPoint(); // Provides migration management in development

        // Enable custom error pages in development
        // Comment out the line below to see detailed developer exception page
        app.UseExceptionHandler("/Home/Error");

        // Enable status code pages for 404, 403, etc. (must come after UseExceptionHandler)
        app.UseStatusCodePagesWithReExecute("/Error/{0}");
    }
    else
    {
        app.UseExceptionHandler("/Home/Error"); // Global exception handler for production
        app.UseHsts(); // HTTP Strict Transport Security (HSTS) for production

        // Enable status code pages for 404, 403, etc. (must come after UseExceptionHandler)
        app.UseStatusCodePagesWithReExecute("/Error/{0}");
    }

    // Tile Cache Service initialization
    using (var scope = app.Services.CreateScope())
    {
        var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        tileCacheService.Initialize();
    }

    // Load upload size limit from settings
    var maxRequestSize = await LoadUploadSizeLimitFromDatabaseAsync();
    app.UseMiddleware<DynamicRequestSizeMiddleware>(maxRequestSize);

    // Force HTTPS in the app
    app.UseHttpsRedirection();

    // Configure routing and authorization
    app.UseRouting();
    app.UseAuthorization();

    // Serve static files (includes runtime-generated files like thumbnails)
    app.UseStaticFiles();

    // Serve documentation at /docs/ - works locally and matches GitHub Pages structure
    var docsPath = Path.Combine(app.Environment.ContentRootPath, "docs");
    if (Directory.Exists(docsPath))
    {
        var docsFileProvider = new PhysicalFileProvider(docsPath);
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = docsFileProvider,
            RequestPath = "/docs"
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = docsFileProvider,
            RequestPath = "/docs"
        });
    }

    // Map static assets (e.g., CSS, JS) to routes
    app.MapStaticAssets();

    // /api/* specific error handling responses
    app.Use(async (context, next) =>
    {
        var isApi = context.Request.Path.StartsWithSegments("/api");

        try
        {
            await next();

            if (isApi && !context.Response.HasStarted)
            {
                var statusCode = context.Response.StatusCode;

                if (statusCode == 401 || statusCode == 403 || statusCode == 404)
                {
                    context.Response.Clear();
                    context.Response.ContentType = "application/json";

                    var result = new
                    {
                        status = statusCode,
                        error = statusCode switch
                        {
                            401 => "Unauthorized",
                            403 => "Forbidden",
                            404 => "Not Found",
                            _ => "Error"
                        },
                        message = statusCode switch
                        {
                            401 => "Authentication is required to access this endpoint.",
                            403 => "You do not have permission to access this resource.",
                            404 => "The requested API endpoint does not exist.",
                            _ => "An error occurred."
                        }
                    };

                    await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                }
            }
        }
        catch (Exception ex)
        {
            if (isApi && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";

                var error = new
                {
                    status = 500,
                    error = "Internal Server Error",
                    message = "An unexpected error occurred.",
                    details = ex.Message
                };

                await context.Response.WriteAsync(JsonSerializer.Serialize(error));
            }
            else
            {
                throw;
            }
        }
    });

    // Define the default route for controllers
    app.MapControllerRoute(
            "default",
            "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets on the controller route

    // Map Razor Pages
    app.MapRazorPages()
        .WithStaticAssets();
}

#region Area Configuration

static void ConfigureAreas(WebApplication app)
{
    // Map Area Controller Route for Admin
    app.MapAreaControllerRoute(
            "admin",
            "Admin", // Area name
            "Admin/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for Manager
    app.MapAreaControllerRoute(
            "manager",
            "Manager", // Area name
            "Manager/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for User
    app.MapAreaControllerRoute(
            "user",
            "User", // Area name
            "User/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for API
    app.MapAreaControllerRoute(
        "api",
        "Api", // Area name
        "Api/{controller=Home}/{action=Index}/{id?}");

    // Map Area Controller Route for Public resources
    app.MapAreaControllerRoute(
            "public",
            "Public", // Area name
            "Public/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets
}

#endregion Area Configuration

// Method to seed the database with initial roles and the admin user
static async Task SeedDatabase(WebApplication app)
{
    // Create a scope for accessing services
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

    // Seed roles and admin user
    await ApplicationDbContextSeed.SeedAsync(userManager, roleManager, services);
}

#endregion Methods

