using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Protects the credential and deployment contracts required for a safe #499 upgrade.</summary>
public sealed class ProviderCredentialUpgradeCorrectionTests
{
    [Fact]
    public void AdministratorRoutingCiphertext_FromPre499Registration_RemainsReadableAfterUpgrade() =>
        WithRecreatedProviders(historical =>
        {
            var configuration = new RoutingProviderConfiguration();
            new RoutingProviderCredentialService(historical).Replace(configuration, "historical-admin-key");
            return configuration;
        }, (configuration, current) =>
        {
            var read = new RoutingProviderCredentialService(current).Read(configuration);

            Assert.True(read.Succeeded);
            Assert.Equal("historical-admin-key", read.Credential);
            Assert.False(new UserRoutingCredentialService(current).Unprotect(
                "wrong-user", Guid.NewGuid(), configuration.CredentialCiphertext).Succeeded);
        });

    [Fact]
    public void PersonalRoutingCiphertext_FromPre499Registration_RemainsReadableAfterUpgrade() =>
        WithRecreatedProviders(historical =>
        {
            var userId = "historical-routing-user";
            var providerId = Guid.NewGuid();
            var ciphertext = new UserRoutingCredentialService(historical)
                .Protect(userId, providerId, "historical-personal-key");
            return (userId, providerId, ciphertext);
        }, (protectedValue, current) =>
        {
            var currentOwner = new UserRoutingCredentialService(current);
            Assert.Equal("historical-personal-key",
                currentOwner.Unprotect(protectedValue.userId, protectedValue.providerId, protectedValue.ciphertext).Credential);
            Assert.False(currentOwner.Unprotect("wrong-user", protectedValue.providerId, protectedValue.ciphertext).Succeeded);
            Assert.False(currentOwner.Unprotect(protectedValue.userId, Guid.NewGuid(), protectedValue.ciphertext).Succeeded);
        });

    [Fact]
    public void DeploymentGuidance_UsesRetainedRingAndRestoresOwnershipAndPermissions()
    {
        var root = FindRepositoryRoot();
        var deployment = File.ReadAllText(Path.Combine(root, "docs", "20-Deployment.md"));
        var providers = File.ReadAllText(Path.Combine(root, "docs", "24-Personal-Location-Providers.md"));
        var currentFiles = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deployment"), "*"))
            .Where(path => Path.GetExtension(path) is ".md" or ".sh" or ".service")
            .Append(Path.Combine(root, "appsettings.Production.json"))
            .ToArray();

        Assert.All(currentFiles, path => Assert.DoesNotContain(
            "/var/lib/wayfarer/data-protection-keys", File.ReadAllText(path), StringComparison.Ordinal));
        Assert.Contains("/home/wayfarer/.aspnet/DataProtection-Keys", deployment, StringComparison.Ordinal);
        Assert.Contains("/home/wayfarer/.aspnet/DataProtection-Keys", providers, StringComparison.Ordinal);
        Assert.Contains("sudo chown -R wayfarer:wayfarer /home/wayfarer/.aspnet/DataProtection-Keys", providers, StringComparison.Ordinal);
        Assert.Contains("sudo chmod 700 /home/wayfarer/.aspnet/DataProtection-Keys", providers, StringComparison.Ordinal);
        Assert.Contains("before starting", providers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database and key ring", providers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/var/www/wayfarer", providers, StringComparison.Ordinal);
        Assert.Contains("containers", providers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multiple hosts", providers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreadable", providers, StringComparison.OrdinalIgnoreCase);
    }

    private static void WithRecreatedProviders<T>(
        Func<IDataProtectionProvider, T> protect, Action<T, IDataProtectionProvider> assertion)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wayfarer-pre499-content-{Guid.NewGuid():N}");
        var ring = Path.Combine(root, "keys");
        Directory.CreateDirectory(ring);
        try
        {
            T protectedValue;
            string historicalDiscriminator;
            using (var historicalServices = BuildProvider(root, ring, useFinalRegistration: false))
            {
                historicalDiscriminator = historicalServices.GetRequiredService<IOptions<DataProtectionOptions>>()
                    .Value.ApplicationDiscriminator
                    ?? throw new InvalidOperationException("The hosted historical discriminator was not configured.");
                protectedValue = protect(historicalServices.GetRequiredService<IDataProtectionProvider>());
            }
            using var currentServices = BuildProvider(root, ring, useFinalRegistration: true);
            Assert.Equal(historicalDiscriminator, currentServices.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
            assertion(protectedValue, currentServices.GetRequiredService<IDataProtectionProvider>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider BuildProvider(string contentRoot, string ring, bool useFinalRegistration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            EnvironmentName = "Production"
        });
        if (useFinalRegistration)
        {
            builder.Configuration["DataProtection:KeyRingPath"] = ring;
            builder.AddWayfarerDataProtection();
        }
        else
        {
            // Pre-#499 relied on ASP.NET Core's host/content-root discriminator without an application name override.
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(ring));
        }
        return builder.Services.BuildServiceProvider();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
