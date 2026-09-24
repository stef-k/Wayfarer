using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.CommandLine;

/// <summary>Runs explicit F1 commands without web startup, seeding, jobs, provider contact or audit logging.</summary>
public static class DataProtectionCli
{
    /// <summary>Builds the same hosted legacy identity and configuration, returning only bounded diagnostics.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || args[1] is not ("status" or "prepare-stable-identity"))
        {
            await error.WriteLineAsync("Usage: data-protection status | prepare-stable-identity");
            return 2;
        }
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            // No framework/provider exceptions or EF parameters may enter CLI output or audit sinks.
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile("appsettings.json", false)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true)
                .AddEnvironmentVariables();
            builder.AddWayfarerDataProtection();
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
                    postgres => postgres.UseNetTopologySuite()));
            builder.Services.AddScoped<PersonalProviderCredentialService>();
            await using var app = builder.Build();
            using var scope = app.Services.CreateScope();
            return await ExecuteAsync(args[1], scope.ServiceProvider.GetRequiredService<StableIdentityPreparation>(), output, error);
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Data Protection command failed; check source authority, configuration and database availability.");
            return 1;
        }
    }

    /// <summary>Executes the bounded command surface against an already scoped authority.</summary>
    internal static async Task<int> ExecuteAsync(string command, StableIdentityPreparation preparation,
        TextWriter output, TextWriter error)
    {
        try
        {
            var status = command == "prepare-stable-identity"
                ? await preparation.PrepareAsync() : await preparation.StatusAsync();
            await output.WriteLineAsync($"Active: {status.Active}; stable-ready: {status.StableReady}; pending: {status.Pending}; blocked: {status.Blocked}; revoked/no-credential: {status.Inactive}");
            await output.WriteLineAsync($"Activation-ready: {status.Ready}; future application name: {DataProtectionAuthority.StableApplicationName}");
            return status.Ready ? 0 : 1;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Data Protection command failed; no preparation changes were committed.");
            return 1;
        }
    }
}
