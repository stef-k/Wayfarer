using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.CommandLine;

/// <summary>Runs explicit offline commands without web startup, seeding, jobs, provider contact or audit logging.</summary>
public static class DataProtectionCli
{
    /// <summary>Builds stable status or the explicit hosted source preparation identity, returning only bounded diagnostics.</summary>
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
            var ring = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment);
            if (args[1] == "prepare-stable-identity")
            {
                // Use the framework's actual hosted source identity, independent of F2 registration.
                builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(ring.Path))
                    .DisableAutomaticKeyGeneration();
                builder.Services.AddScoped<StableIdentityReadiness>();
                builder.Services.AddSingleton(new StableDataProtectionProvider(DataProtectionProvider.Create(
                    new DirectoryInfo(ring.Path), options => options
                        .SetApplicationName(DataProtectionAuthority.StableApplicationName).DisableAutomaticKeyGeneration())));
                builder.Services.AddScoped<LegacyCredentialPreparationCodec>();
                builder.Services.AddScoped<StableIdentityPreparation>();
                builder.Services.AddScoped(services => new PersonalProviderCredentialService(
                    services.GetRequiredService<StableDataProtectionProvider>().Provider));
            }
            else
            {
                builder.AddWayfarerDataProtection(readOnlyKeys: true);
                builder.Services.AddScoped<PersonalProviderCredentialService>();
            }
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
                    postgres => postgres.UseNetTopologySuite()));
            await using var app = builder.Build();
            using var scope = app.Services.CreateScope();
            await output.WriteLineAsync($"Key-ring path: {ring.Path}; authority: {ring.Authority}");
            if (args[1] == "prepare-stable-identity")
                await scope.ServiceProvider.GetRequiredService<StableIdentityPreparation>().PrepareAsync();
            var status = await scope.ServiceProvider.GetRequiredService<StableIdentityReadiness>().StatusAsync();
            return await WriteStatusAsync(status, output);
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
            return await WriteStatusAsync(status, output);
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Data Protection command failed; no preparation changes were committed.");
            return 1;
        }
    }

    /// <summary>Emits only bounded readiness categories and the stable public application name.</summary>
    private static async Task<int> WriteStatusAsync(StableIdentityStatus status, TextWriter output)
    {
        await output.WriteLineAsync($"Active: {status.Active}; stable-ready: {status.StableReady}; pending: {status.Pending}; blocked: {status.Blocked}; revoked/no-credential: {status.Inactive}");
        await output.WriteLineAsync($"Activation-ready: {status.Ready}; application name: {DataProtectionAuthority.StableApplicationName}");
        return status.Ready ? 0 : 1;
    }
}
