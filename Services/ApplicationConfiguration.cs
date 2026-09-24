using Serilog;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>Composes application configuration and registers the staged storage authority.</summary>
internal static class ApplicationConfiguration
{
    /// <summary>Resolves and registers the single runtime path authority before logging starts.</summary>
    internal static StoragePaths Configure(WebApplicationBuilder builder)
    {
        // Adding JSON configuration files to the app's configuration pipeline
        // Environment variables are added last to ensure they override JSON settings (e.g., connection strings from systemd)
        builder.Configuration.AddJsonFile("appsettings.json", false, true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true, true)
            .AddEnvironmentVariables();

        // Resolve once before Serilog; DI receives this exact same immutable authority.
        var options = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        var paths = new StoragePaths(Microsoft.Extensions.Options.Options.Create(options), builder.Environment);
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<TripThumbnailStorage>();
        builder.Services.AddSingleton<TileCacheStorage>();
        builder.Services.AddSingleton<Wayfarer.Services.LocationImports.LocationImportStagedFiles>();
        return paths;
    }

    /// <summary>Preserves console, daily file and audit sinks using the shared operational root.</summary>
    internal static void ConfigureLogging(WebApplicationBuilder builder, StoragePaths storagePaths)
    {
        // Fail startup on an unusable operational root rather than falling back to the app tree.
        Directory.CreateDirectory(storagePaths.LogRoot);
        var logFilePath = Path.Combine(storagePaths.LogRoot, "wayfarer-.log");
        var today = Path.Combine(storagePaths.LogRoot, $"wayfarer-{DateTime.Now:yyyyMMdd}.log");
        using (new FileStream(today, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }

        // Configure Serilog for logging to console, file, and PostgreSQL.
        // .Enrich.FromLogContext() enables LogContext properties (e.g., RequestId pushed by
        // RequestIdLoggingMiddleware) to flow into all sinks automatically.
        // {Properties:j} in output templates renders pushed properties as JSON.
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day, outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
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
}
