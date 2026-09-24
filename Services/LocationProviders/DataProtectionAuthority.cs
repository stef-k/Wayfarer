using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;


namespace Wayfarer.Services.LocationProviders;

/// <summary>Configures and validates the persistent single-host Data Protection authority.</summary>
public static class DataProtectionAuthority
{
    /// <summary>Names the future application identity, independent of releases and hosted paths.</summary>
    public const string StableApplicationName = "Wayfarer";

    /// <summary>Registers one explicit persistent key ring shared by every application protector.</summary>
    public static void AddWayfarerDataProtection(this WebApplicationBuilder builder)
    {
        var configured = builder.Configuration["DataProtection:KeyRingPath"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wayfarer", "DataProtectionKeys")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(path);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(path));
        builder.Services.AddSingleton(new DataProtectionKeyRing(path));
        // The secondary provider reads the same ring but never generates or modifies keys.
        builder.Services.AddSingleton(new StableDataProtectionProvider(
            DataProtectionProvider.Create(new DirectoryInfo(path), configuration =>
                configuration.SetApplicationName(StableApplicationName).DisableAutomaticKeyGeneration())));
        builder.Services.AddScoped<StableIdentityPreparation>();
    }

    /// <summary>Fails startup when the key ring cannot round-trip or retained protected credentials cannot be read.</summary>
    public static async Task ValidateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var keyRing = scope.ServiceProvider.GetRequiredService<DataProtectionKeyRing>();
        var stable = scope.ServiceProvider.GetRequiredService<StableDataProtectionProvider>().Provider;
        var probe = provider.CreateProtector("Wayfarer.DataProtection.StartupProbe.v1");
        var stableProbe = stable.CreateProtector("Wayfarer.DataProtection.StartupProbe.v1");
        try
        {
            var probeFile = Path.Combine(keyRing.Path, $".write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probeFile, "probe", cancellationToken);
            try
            {
                if (await File.ReadAllTextAsync(probeFile, cancellationToken) != "probe")
                    throw new IOException();
            }
            finally { File.Delete(probeFile); }
            if (probe.Unprotect(probe.Protect("ready")) != "ready") throw new InvalidOperationException();
            if (stableProbe.Unprotect(stableProbe.Protect("ready")) != "ready") throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            throw new InvalidOperationException("The configured Data Protection key authority is unusable.");
        }

        var status = await scope.ServiceProvider.GetRequiredService<StableIdentityPreparation>().StatusAsync(cancellationToken);
        if (status.Blocked != 0)
            throw new InvalidOperationException("A personal provider credential is unreadable or inconsistent with the configured key authority.");
        if (status.Pending != 0)
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DataProtectionAuthority))
                .LogWarning("Stable identity preparation pending for {Count} credential profiles.", status.Pending);
    }
}

/// <summary>Describes the configured durable key-ring path without exposing key material.</summary>
public sealed record DataProtectionKeyRing(string Path);

/// <summary>Explicitly identifies the secondary F1 provider; never replaces the global legacy provider.</summary>
public sealed record StableDataProtectionProvider(IDataProtectionProvider Provider);
