using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Defines the initial persisted and service-level contract for external routing configuration.
/// </summary>
public sealed class ExternalRoutingConfigurationContractTests
{
    [Fact]
    public void Configuration_RequiresEndpointVerificationCoordinatesAndMapping()
    {
        var configuration = new RoutingProviderConfiguration
        {
            DisplayName = "Self-hosted OSRM",
            AdapterType = RoutingAdapterType.OsrmCompatible
        };

        var state = RoutingProviderStateResolver.Resolve(configuration, isActive: false);

        Assert.Equal(RoutingProviderState.Incomplete, state);
        Assert.Equal(1, configuration.ConfigurationVersion);
    }

    [Fact]
    public void ActiveProvider_IsOwnedOnlyBySingletonApplicationSettings()
    {
        var providerId = Guid.NewGuid();
        var settings = new ApplicationSettings
        {
            Id = 1,
            ExternalRouteGenerationEnabled = true,
            ActiveRoutingProviderConfigurationId = providerId
        };

        Assert.Equal(providerId, settings.ActiveRoutingProviderConfigurationId);
        Assert.DoesNotContain(
            typeof(RoutingProviderConfiguration).GetProperties(),
            property => property.Name.Equals("IsActive", StringComparison.Ordinal));
    }

    [Fact]
    public void BlankCredentialEdit_PreservesProtectedCredential()
    {
        var protector = new EphemeralDataProtectionProvider()
            .CreateProtector(RoutingProviderCredentialService.ProtectionPurpose);
        var service = new RoutingProviderCredentialService(protector);
        var configuration = new RoutingProviderConfiguration();
        service.Replace(configuration, "secret");
        var ciphertext = configuration.CredentialCiphertext;
        var version = configuration.ConfigurationVersion;

        service.ApplyEdit(configuration, "  ");

        Assert.Equal(ciphertext, configuration.CredentialCiphertext);
        Assert.Equal(version, configuration.ConfigurationVersion);
        Assert.True(configuration.CredentialPresent);
    }

    [Fact]
    public void RelevantConfigurationChange_InvalidatesVerificationAndIncrementsVersion()
    {
        var configuration = new RoutingProviderConfiguration
        {
            ConfigurationVersion = 4,
            VerifiedConfigurationVersion = 4
        };

        configuration.MarkConfigurationChanged();

        Assert.Equal(5, configuration.ConfigurationVersion);
        Assert.Null(configuration.VerifiedConfigurationVersion);
    }

    [Fact]
    public async Task DisabledFeature_RejectsGenerationBeforeProviderContact()
    {
        var generator = new ExternalRouteProposalGenerator(
            () => new ApplicationSettings { ExternalRouteGenerationEnabled = false });

        var result = await generator.GenerateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("external-routing-disabled", result.ErrorCode);
    }
}
