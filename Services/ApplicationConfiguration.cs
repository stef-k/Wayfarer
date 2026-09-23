using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>Composes application configuration and registers the staged storage authority.</summary>
internal static class ApplicationConfiguration
{
    /// <summary>Preserves native configuration/log-directory setup and lazily registers future storage paths.</summary>
    internal static void Configure(WebApplicationBuilder builder)
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

        // Resolve on demand; location imports adopt durable storage while other subsystems transition separately.
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
        builder.Services.AddSingleton<StoragePaths>();
        builder.Services.AddSingleton<Wayfarer.Services.LocationImports.LocationImportStagedFiles>();
    }
}
