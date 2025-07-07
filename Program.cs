using System.Collections.Specialized;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using NetTopologySuite.Geometries;
using Microsoft.Extensions.DependencyInjection;  // for AddQuartz(), AddQuartzHostedService()
using Quartz;
using Quartz.Impl; // for UseMicrosoftDependencyInjectionJobFactory(), UsePersistentStore(), etc.
using Quartz.Spi;                               // for IJobFactory
using Quartz.Serialization.Json;                // for UseNewtonsoftJsonSerializer()
using Serilog;
using Wayfarer.Jobs;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Swagger;
using Wayfarer.Util;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#region CLI Command Handling

// Handling the "reset-password" command from the CLI
if (args.Length > 0 && args[0] == "reset-password")
{
    await HandlePasswordResetCommand(args);
}

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

#region Quartz Configuration

// Configuring Quartz for job scheduling
ConfigureQuartz(builder);

#endregion Quartz Configuration

#region Configure other services

ConfigureServices(builder);

#endregion Configure other services

WebApplication app = builder.Build();

// Check and set if needed for Quartz database setup for job persistence 
await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(app.Services);

#region Middleware Setup

// Setting up middleware components, including performance monitoring and error handling
ConfigureAreas(app);
ConfigureMiddleware(app).GetAwaiter().GetResult();

#endregion Middleware Setup

#region Database Seeding

// Seed the database with roles and the admin user if necessary
await SeedDatabase(app);

#endregion Database Seeding

app.Run();

static async Task<long> LoadUploadSizeLimitFromDatabaseAsync()
{
    // Your logic to load the size limit from the database
    return 100 * 1024 * 1024; // example: 100MB
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

    string username = args[1];
    string newPassword = args[2];

    // Rebuild services to handle the password reset
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"), x => x.UseNetTopologySuite()));
    builder.Services.AddDefaultIdentity<ApplicationUser>()
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

    ServiceProvider services = builder.Services.BuildServiceProvider();
    
    UserManager<ApplicationUser> userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    builder.Services.AddHttpContextAccessor();
    ApplicationUser? user = await userManager.FindByNameAsync(username);
    if (user == null)
    {
        Console.WriteLine($"User '{username}' not found.");
        return;
    }

    string token = await userManager.GeneratePasswordResetTokenAsync(user);
    IdentityResult result = await userManager.ResetPasswordAsync(user, token, newPassword);

    if (result.Succeeded)
    {
        Console.WriteLine($"Password for user '{username}' has been reset successfully.");
    }
    else
    {
        Console.WriteLine("Failed to reset password. Errors:");
        foreach (IdentityError error in result.Errors)
        {
            Console.WriteLine($" - {error.Description}");
        }
    }
}

// Method to configure the application�s configuration settings
static void ConfigureConfiguration(WebApplicationBuilder builder)
{
    // Adding JSON configuration files to the app's configuration pipeline
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                         .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

    // Retrieving the log file path from the configuration
    string? logFilePath = builder.Configuration["Logging:LogFilePath:Default"];

    if (string.IsNullOrEmpty(logFilePath))
    {
        throw new InvalidOperationException("Log file path is not configured. Please check your appsettings.json or appsettings.Development.json.");
    }

    // Ensuring that the directory for logs exists
    string? logDirectory = Path.GetDirectoryName(logFilePath);
    if (!Directory.Exists(logDirectory))
    {
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
}

// Method to configure logging with Serilog
static void ConfigureLogging(WebApplicationBuilder builder)
{
    // Retrieve the log file path from configuration
    string? logFilePath = builder.Configuration["Logging:LogFilePath:Default"];

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
    string connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

    // Add DbContext to the DI container, configure it with PostgreSQL and NetTopologySuite for spatial data
    // builder.Services.AddDbContext<ApplicationDbContext>(options =>
    //     options.UseNpgsql(connectionString, x => x.UseNetTopologySuite()));
    
    // use a pool of db connections instead of spawning a new per request
    builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
        options.UseNpgsql(connectionString, x => x.UseNetTopologySuite()));

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
    builder.Services.AddTransient<AuditLogCleanupJob>(); // if you have a separate class
    // ...and any other IJob implementations you’ll use

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
            ["quartz.scheduler.instanceName"]           = "QuartzScheduler",
            ["quartz.scheduler.instanceId"]             = "AUTO",
            ["quartz.jobStore.type"]                    = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.driverDelegateType"]      = "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
            ["quartz.jobStore.tablePrefix"]             = "qrtz_",
            ["quartz.jobStore.useProperties"]           = "true",
            ["quartz.jobStore.dataSource"]              = "default",
            ["quartz.dataSource.default.provider"]      = "Npgsql",
            ["quartz.dataSource.default.connectionString"] = cs,
            ["quartz.serializer.type"]                  = "Quartz.Simpl.JsonObjectSerializer, Quartz.Serialization.Json"
        };

        var factory   = new StdSchedulerFactory(props);
        var scheduler = factory.GetScheduler().Result;

        scheduler.JobFactory = sp.GetRequiredService<IJobFactory>();
        using var scope = sp.CreateScope();
        scheduler.ListenerManager.AddJobListener(scope.ServiceProvider.GetRequiredService<IJobListener>());

        scheduler.Start().Wait();

        // Schedule maintenance jobs once if missing
        var logJobKey   = new JobKey("LogCleanupJob", "Maintenance");
        var auditJobKey = new JobKey("AuditLogCleanupJob", "Maintenance");

        if (!scheduler.CheckExists(logJobKey).Result)
        {
            var job = JobBuilder.Create<LogCleanupJob>()
                                .WithIdentity(logJobKey)
                                .StoreDurably(true)
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
                                .StoreDurably(true)
                                .Build();

            var trigger = TriggerBuilder.Create()
                                        .ForJob(job)
                                        .WithIdentity("AuditLogCleanupTrigger", "Maintenance")
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
        .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);;
    

    // Add Swagger generation
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Wayfarer API" });

        // Custom Point converter in Swagger
        // Directly configure how 'Point' is represented in Swagger UI
        c.MapType<Point>(() => new OpenApiSchema
        {
            Type = "string",
            Format = "wkt",  // Optional: specify Well-Known Text format (WKT)
            Description = "The coordinates in WKT format (Point)",
            Example = new OpenApiString("48.8588443, 2.2943506"),  // Example of WKT format
            Nullable = false  // Explicitly set 'Nullable' to false
        });


        // Apply the schema filter to hide PostGIS types
        c.DocumentFilter<RemovePostGisSchemasDocumentFilter>();

        // Use a predicate to include only actions within the "Api" area
        c.DocInclusionPredicate((docName, apiDesc) =>
        {
            Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor actionDescriptor = apiDesc.ActionDescriptor;

            // Check if the action descriptor has the "area" route value set to "Api"
            return actionDescriptor.RouteValues.ContainsKey("area") &&
                   actionDescriptor.RouteValues["area"] == "Api";
        });

    });

    // PostGIS POINT JSON converter for JSON serialization
    builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new PointJsonConverter());
    });

    // Reverse geocoding Mapbox service
    builder.Services.AddHttpClient<ReverseGeocodingService>();
    
    // Tile Cache service
    builder.Services.AddScoped<TileCacheService>();
    
    // add the Http client to the Tile Cache service
    builder.Services.AddHttpClient<TileCacheService>();
    
    // Location service, handles location results per zoom and bounds levels
    builder.Services.AddScoped<LocationService>();
    
    // Server Send Events Service setup (SSE) used to broadcast messages to clients
    builder.Services.AddSingleton<SseService>();
    
    // User location stats service
    builder.Services.AddScoped<ILocationStatsService, LocationStatsService>();
    
    // Trip export service (PDF, KML, Google MyMaps KML)
    builder.Services.AddScoped<ITripExportService, TripExportService>();
    
    builder.Services.AddScoped<IRazorViewRenderer, RazorViewRenderer>();
    builder.Services.AddSingleton<MapSnapshotService>();
    
    // Trip import service
    builder.Services.AddScoped<ITripImportService, TripImportService>();
}

// Method to configure middleware components such as error handling and performance monitoring
static async Task ConfigureMiddleware(WebApplication app)
{
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
    }
    else
    {
        app.UseExceptionHandler("/Home/Error"); // Global exception handler for production
        app.UseHsts(); // HTTP Strict Transport Security (HSTS) for production
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

    // Map static assets (e.g., CSS, JS) to routes
    app.MapStaticAssets();

    // Define the default route for controllers
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
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
        name: "admin",
        areaName: "Admin", // Area name
        pattern: "Admin/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for Manager
    app.MapAreaControllerRoute(
        name: "manager",
        areaName: "Manager", // Area name
        pattern: "Manager/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for User
    app.MapAreaControllerRoute(
        name: "user",
        areaName: "User", // Area name
        pattern: "User/{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets(); // Enable static assets

    // Map Area Controller Route for API
    app.MapAreaControllerRoute(
        name: "api",
        areaName: "Api", // Area name
        pattern: "Api/{controller=Home}/{action=Index}/{id?}");

    // Map Area Controller Route for Public resources
    app.MapAreaControllerRoute(
    name: "public",
    areaName: "Public", // Area name
    pattern: "Public/{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets(); // Enable static assets
}

#endregion Area Configuration

// Method to seed the database with initial roles and the admin user
static async Task SeedDatabase(WebApplication app)
{
    // Create a scope for accessing services
    using IServiceScope scope = app.Services.CreateScope();
    IServiceProvider services = scope.ServiceProvider;
    UserManager<ApplicationUser> userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    RoleManager<IdentityRole> roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

    // Seed roles and admin user
    await ApplicationDbContextSeed.SeedAsync(userManager, roleManager, services);
}

#endregion Methods