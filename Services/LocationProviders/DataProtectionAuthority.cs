using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Configures and validates the persistent single-host Data Protection authority.</summary>
public static class DataProtectionAuthority
{
    /// <summary>Names the stable application identity, independent of releases and hosted paths.</summary>
    public const string StableApplicationName = "Wayfarer";

    /// <summary>Registers the stable runtime identity and the single resolved ring.</summary>
    public static void AddWayfarerDataProtection(this WebApplicationBuilder builder, bool readOnlyKeys = false)
    {
        var ring = ResolveKeyRing(builder.Configuration, builder.Environment);
        if (!readOnlyKeys) Directory.CreateDirectory(ring.Path);
        var protection = builder.Services.AddDataProtection()
            .SetApplicationName(StableApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(ring.Path));
        if (readOnlyKeys) protection.DisableAutomaticKeyGeneration();
        builder.Services.AddSingleton(ring);
        builder.Services.AddScoped<StableIdentityReadiness>();
    }

    /// <summary>Selects one authority in place, refusing competing default rings without an explicit override.</summary>
    public static DataProtectionKeyRing ResolveKeyRing(IConfiguration configuration, IHostEnvironment environment,
        string? previousDefault = null)
    {
        var configured = configuration["DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return new(Path.GetFullPath(configured), "explicit override");
        var storage = new StoragePaths(Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection("Storage").Get<Wayfarer.Models.Options.StorageOptions>() ?? new()), environment);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage.DataProtection));
        var previous = Path.TrimEndingDirectorySeparator(Path.GetFullPath(previousDefault ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wayfarer", "DataProtectionKeys")));
        var distinct = !string.Equals(current, previous,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (distinct && HasKeys(previous))
        {
            if (HasKeys(current))
                throw new InvalidOperationException("Ambiguous Data Protection rings; inspect the complete ring and set DataProtection:KeyRingPath explicitly.");
            return new(previous, "previous-default compatibility");
        }
        return new(current, "current Storage default");
    }

    /// <summary>Detects ordinary framework key filenames without reading key contents or identifiers.</summary>
    private static bool HasKeys(string path) => Directory.Exists(path) &&
        Directory.EnumerateFiles(path, "key-*.xml", SearchOption.TopDirectoryOnly).Any();

    /// <summary>Fails startup when the key ring cannot round-trip or retained protected credentials cannot be read.</summary>
    public static async Task ValidateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var keyRing = scope.ServiceProvider.GetRequiredService<DataProtectionKeyRing>();
        if (scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator != StableApplicationName)
            throw new InvalidOperationException("Data Protection application identity must be Wayfarer.");
        var probe = provider.CreateProtector("Wayfarer.DataProtection.StartupProbe.v1");
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            throw new InvalidOperationException("The configured Data Protection key authority is unusable.");
        }

        var status = await scope.ServiceProvider.GetRequiredService<StableIdentityReadiness>().StatusAsync(cancellationToken);
        if (status.Blocked != 0)
            throw new InvalidOperationException("A personal provider credential is unreadable or inconsistent with the configured key authority.");
        if (status.Pending != 0)
            throw new InvalidOperationException("Stable credentials are unprepared; stop and run source prepare-stable-identity before activation.");
    }
}

/// <summary>Describes the configured durable key-ring path without exposing key material.</summary>
public sealed record DataProtectionKeyRing(string Path, string Authority);
