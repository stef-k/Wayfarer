using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Configures and validates the persistent single-host Data Protection authority.</summary>
public static class DataProtectionAuthority
{
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
    }

    /// <summary>Fails startup when the key ring cannot round-trip or retained protected credentials cannot be read.</summary>
    public static async Task ValidateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var keyRing = scope.ServiceProvider.GetRequiredService<DataProtectionKeyRing>();
        var probe = provider.CreateProtector("Wayfarer.DataProtection.StartupProbe.v1");
        try
        {
            var probeFile = Path.Combine(keyRing.Path, $".write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probeFile, "probe", cancellationToken);
            File.Delete(probeFile);
            if (probe.Unprotect(probe.Protect("ready")) != "ready") throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            throw new InvalidOperationException("The configured Data Protection key authority is unusable.", exception);
        }

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var personal = scope.ServiceProvider.GetRequiredService<PersonalProviderCredentialService>();
        foreach (var profile in await db.Set<PersonalLocationProviderProfile>().AsNoTracking()
                     .Where(item => item.ProtectedCredential != null && item.RevokedAt == null).ToListAsync(cancellationToken))
            if (!personal.Read(profile).Succeeded)
                throw new InvalidOperationException("A protected personal provider credential is unreadable with the configured key authority.");

    }
}

/// <summary>Describes the configured durable key-ring path without exposing key material.</summary>
public sealed record DataProtectionKeyRing(string Path);
