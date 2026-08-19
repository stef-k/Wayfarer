using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the production contracts for administrator-approved personal routing.</summary>
public sealed class PersonalRoutingContractTests
{
    [Fact]
    public void RetainedConfiguration_DefaultPersonalDefaultTransitionsAreMonotonic()
    {
        var configuration = UserRoutingConfiguration.CreateServerDefault("user-1");
        var providerId = Guid.NewGuid();

        configuration.SelectPersonalProvider(providerId);
        configuration.UseServerDefault();

        Assert.Null(configuration.SelectedProviderConfigurationId);
        Assert.Equal(3, configuration.ConfigurationVersion);
        Assert.False(configuration.CredentialPresent);
    }

    [Fact]
    public void CredentialProtectionIsBoundToUserAndProvider()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = new UserRoutingCredentialService(provider);
        var providerId = Guid.NewGuid();
        var ciphertext = service.Protect("user-1", providerId, "secret");

        Assert.Equal("secret", service.Unprotect("user-1", providerId, ciphertext).Credential);
        Assert.False(service.Unprotect("user-2", providerId, ciphertext).Succeeded);
        Assert.False(service.Unprotect("user-1", Guid.NewGuid(), ciphertext).Succeeded);
    }

    [Fact]
    public void PersonalEligibilityDistinguishesRequiredAndCredentialFreeTemplates()
    {
        var required = EligibleProvider(PersonalRoutingAccess.CredentialRequired);
        var credentialFree = EligibleProvider(PersonalRoutingAccess.CredentialFree);

        Assert.True(PersonalRoutingEligibility.Evaluate(required).Eligible);
        Assert.True(PersonalRoutingEligibility.Evaluate(required).CredentialRequired);
        Assert.True(PersonalRoutingEligibility.Evaluate(credentialFree).Eligible);
        Assert.False(PersonalRoutingEligibility.Evaluate(credentialFree).CredentialRequired);
    }

    [Fact]
    public void UnavailablePersonalResolutionIsExplicitAndTerminal()
    {
        var result = AuthoritativeRoutingProviderResolver.UnavailablePersonal("personal-provider-unavailable");

        Assert.Equal(RoutingProviderResolutionOutcome.UnavailablePersonal, result.Outcome);
        Assert.False(result.MayResolveServerDefault);
    }

    [Fact]
    public void ProposalContextBindsSelectionModeAndUserConfigurationVersion()
    {
        var binding = new ExternalRouteProposalBinding(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user-1", "geometry", "anchors",
            Guid.NewGuid(), Guid.NewGuid(), 4, 2, "aggregate",
            RoutingProviderSelectionMode.Personal, 9);

        Assert.Equal(RoutingProviderSelectionMode.Personal, binding.ProviderSelectionMode);
        Assert.Equal(9, binding.UserRoutingConfigurationVersion);
    }

    [Fact]
    public void PersonalVerificationCommitAuthorityLocksProviderBeforeUser()
    {
        Assert.Equal(
            [PersonalRoutingVerificationLock.Provider, PersonalRoutingVerificationLock.UserRoutingConfiguration],
            PersonalRoutingVerificationService.CommitLockOrder);
    }

    private static RoutingProviderConfiguration EligibleProvider(PersonalRoutingAccess access)
    {
        var profile = new TransportProfile { Id = Guid.NewGuid(), IsActive = true };
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Approved", BaseEndpoint = "https://routing.example",
            Enabled = true, ConfigurationVersion = 3, VerifiedConfigurationVersion = 3,
            PersonalRoutingAccess = access, Attribution = "Attribution",
            ExternalCoordinateDisclosure = "Coordinates are disclosed.",
            VerificationFromLongitude = 1, VerificationFromLatitude = 2,
            VerificationToLongitude = 3, VerificationToLatitude = 4
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = "driving"
        });
        return provider;
    }
}
