using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;

/// <summary>Reuses the guarded PG18 policy and proves managed fixture/app connection equality.</summary>
public static class BrowserFixtureGuard
{
    /// <summary>Rejects unsafe database names before connecting, then probes the actual server.</summary>
    internal static async Task ValidateAsync(string connection)
    {
        string? effective = null;
        if (Environment.GetEnvironmentVariable("WAYFARER_E2E_MANAGED") == "1")
        {
            var builder = ApplicationBuilder();
            DatabaseSecret.Apply(builder.Configuration);
            effective = builder.Configuration.GetConnectionString("DefaultConnection");
        }
        ValidateConfiguration(connection, effective);
        await PostgresTestServer.ValidateAsync(connection);
        if (effective != null) await PostgresTestServer.ValidateAsync(effective);
    }

    /// <summary>Rejects configuration aliases that could redirect managed application writes before any connection.</summary>
    public static void ValidateConfiguration(string connection, string? effective = null)
    {
        var configured = new NpgsqlConnectionStringBuilder(connection);
        if (configured.Database != "wayfarer_import_tests" || !string.IsNullOrEmpty(configured.SearchPath))
            throw new InvalidOperationException("Browser fixtures require the guarded database and default schema.");
        if (effective != null && !configured.EquivalentTo(new NpgsqlConnectionStringBuilder(effective)))
            throw new InvalidOperationException("Effective application database differs from guarded fixture authority.");
    }

    /// <summary>Loads the same application/environment JSON and final environment overrides as web startup.</summary>
    internal static WebApplicationBuilder ApplicationBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApplicationDbContext).Assembly.GetName().Name,
            ContentRootPath = Directory.GetCurrentDirectory(), Args = []
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile("appsettings.json", false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true)
            .AddEnvironmentVariables();
        return builder;
    }
}
