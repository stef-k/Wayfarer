using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves personal resolution is authoritative and isolated from global credentials.</summary>
public sealed class AuthoritativePersonalRoutingResolverTests : TestBase
{
    [Fact]
    public async Task PersonalModeUsesOnlyUserCredentialWhenGlobalCiphertextIsCorrupt()
    {
        var fixture = CreatePersonalFixture();
        fixture.Provider.CredentialRequired = true;
        fixture.Provider.CredentialPresent = true;
        fixture.Provider.CredentialCiphertext = "corrupt-global-ciphertext";
        fixture.Db.SaveChanges();

        var result = await fixture.Resolver.ResolveAsync(fixture.UserId, fixture.ProfileId, CancellationToken.None);

        Assert.Equal(RoutingProviderResolutionOutcome.ResolvedPersonal, result.Outcome);
        Assert.Equal("personal-secret", result.Execution!.Credential);
        Assert.Equal(RoutingProviderSelectionMode.Personal, result.Execution.SelectionMode);
    }

    [Fact]
    public async Task CorruptPersonalCiphertextIsTerminalAndNeverFallsBackToValidGlobalProvider()
    {
        var fixture = CreatePersonalFixture();
        fixture.Configuration.CredentialCiphertext = "corrupt-personal-ciphertext";
        fixture.Db.SaveChanges();

        var result = await fixture.Resolver.ResolveAsync(fixture.UserId, fixture.ProfileId, CancellationToken.None);

        Assert.Equal(RoutingProviderResolutionOutcome.UnavailablePersonal, result.Outcome);
        Assert.Null(result.Execution);
        Assert.False(result.MayResolveServerDefault);
    }

    [Fact]
    public async Task CredentialFreePersonalModeRejectsStoredCredentialState()
    {
        var fixture = CreatePersonalFixture(PersonalRoutingAccess.CredentialFree);

        var result = await fixture.Resolver.ResolveAsync(fixture.UserId, fixture.ProfileId, CancellationToken.None);

        Assert.Equal(RoutingProviderResolutionOutcome.UnavailablePersonal, result.Outcome);
        Assert.Null(result.Execution);
    }

    private PersonalFixture CreatePersonalFixture(
        PersonalRoutingAccess access = PersonalRoutingAccess.CredentialRequired)
    {
        const string userId = "owner";
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var protection = new EphemeralDataProtectionProvider();
        var userCredentials = new UserRoutingCredentialService(protection);
        var providerCredentials = new RoutingProviderCredentialService(protection);
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Approved", BaseEndpoint = "https://routing.example",
            Enabled = true, PersonalRoutingAccess = access, ConfigurationVersion = 4,
            VerifiedConfigurationVersion = 4, Attribution = "Attribution",
            ExternalCoordinateDisclosure = "Coordinates leave Wayfarer."
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = "driving"
        });
        var configuration = UserRoutingConfiguration.CreateServerDefault(userId);
        configuration.SelectPersonalProvider(provider.Id);
        userCredentials.Replace(configuration, provider.Id, "personal-secret");
        configuration.VerifiedUserConfigurationVersion = configuration.ConfigurationVersion;
        configuration.VerifiedProviderConfigurationVersion = provider.ConfigurationVersion;
        configuration.VerificationStatus = "verified";
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.Set<UserRoutingConfiguration>().Add(configuration);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = true,
            ActiveRoutingProviderConfigurationId = provider.Id
        });
        db.SaveChanges();
        var resolver = new AuthoritativeRoutingProviderResolver(db, providerCredentials, userCredentials);
        return new PersonalFixture(db, resolver, provider, configuration, userId, profile.Id);
    }

    private sealed record PersonalFixture(
        ApplicationDbContext Db, AuthoritativeRoutingProviderResolver Resolver,
        RoutingProviderConfiguration Provider, UserRoutingConfiguration Configuration,
        string UserId, Guid ProfileId);
}
