using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves settings mutations are scoped solely to the authenticated user identity.</summary>
public sealed class UserRoutingAuthorizationTests : TestBase
{
    [Fact]
    public async Task ForeignUserMutationReturnsMissingWithoutChangingOwnerState()
    {
        const string ownerId = "owner";
        var db = CreateDbContext();
        var owner = UserRoutingConfiguration.CreateServerDefault(ownerId);
        db.Set<UserRoutingConfiguration>().Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true });
        db.SaveChanges();
        var service = new UserRoutingConfigurationService(db,
            new UserRoutingCredentialService(new EphemeralDataProtectionProvider()));

        var result = await service.SaveAsync(
            "foreign-user", Guid.NewGuid(), "never-persisted", 0, CancellationToken.None);

        Assert.True(result.Missing);
        Assert.Null(owner.SelectedProviderConfigurationId);
        Assert.False(owner.CredentialPresent);
        Assert.DoesNotContain(db.AuditLogs, item => item.UserId == "foreign-user");
    }

    [Fact]
    public async Task SameCredentialFreeProviderSaveNormalizesContradictoryPersonalState()
    {
        const string userId = "owner";
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First(item => item.IsActive);
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Credential-free", BaseEndpoint = "https://routing.example",
            Enabled = true, PersonalRoutingAccess = PersonalRoutingAccess.CredentialFree,
            ConfigurationVersion = 2, VerifiedConfigurationVersion = 2,
            Attribution = "Attribution", ExternalCoordinateDisclosure = "Coordinates leave Wayfarer."
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = "driving"
        });
        var configuration = UserRoutingConfiguration.CreateServerDefault(userId);
        configuration.SelectedProviderConfigurationId = provider.Id;
        configuration.CredentialCiphertext = "contradictory-ciphertext";
        configuration.CredentialPresent = true;
        configuration.VerifiedUserConfigurationVersion = configuration.ConfigurationVersion;
        configuration.VerifiedProviderConfigurationVersion = provider.ConfigurationVersion;
        configuration.VerificationStatus = "verified";
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.Set<UserRoutingConfiguration>().Add(configuration);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true });
        db.SaveChanges();
        var originalVersion = configuration.ConfigurationVersion;
        var service = new UserRoutingConfigurationService(db,
            new UserRoutingCredentialService(new EphemeralDataProtectionProvider()));

        var result = await service.SaveAsync(
            userId, provider.Id, " ", configuration.RowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(provider.Id, configuration.SelectedProviderConfigurationId);
        Assert.Null(configuration.CredentialCiphertext);
        Assert.False(configuration.CredentialPresent);
        Assert.Null(configuration.VerifiedUserConfigurationVersion);
        Assert.Null(configuration.VerifiedProviderConfigurationVersion);
        Assert.Null(configuration.VerificationStatus);
        Assert.Equal(originalVersion + 1, configuration.ConfigurationVersion);
    }

    [Fact]
    public async Task ForeignUserVerificationReturnsBoundedStaleWithoutChangingOwnerState()
    {
        var db = CreateDbContext();
        var owner = UserRoutingConfiguration.CreateServerDefault("owner");
        db.Set<UserRoutingConfiguration>().Add(owner);
        db.SaveChanges();
        var verification = new PersonalRoutingVerificationService(db, null!, null!, null!);

        var result = await verification.VerifyAsync("foreign-user", owner.RowVersion, CancellationToken.None);

        Assert.Equal("personal-routing-stale", result.ErrorCode);
        Assert.Null(owner.VerificationStatus);
        Assert.Empty(db.AuditLogs);
    }
}
