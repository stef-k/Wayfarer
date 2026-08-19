using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves singleton provider activation serialization on the guarded PostgreSQL database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ExternalRoutingActivationPostgresTests
{
    private readonly PostgresImportTestFixture _fixture;

    /// <summary>Initializes the guarded relational fixture.</summary>
    public ExternalRoutingActivationPostgresTests(PostgresImportTestFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task ConcurrentActivation_SelectsExactlyOneCandidateAndReturnsOneBoundedConflict()
    {
        _fixture.RequireAvailable();
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        Guid? originalActive;
        bool originalEnabled;
        int originalGeneration;
        uint settingsRowVersion;
        bool createdSettings = false, createdProfile = false;
        Guid profileId;
        await using (var setup = _fixture.CreateContext())
        {
            var settings = await setup.ApplicationSettings.SingleOrDefaultAsync(item => item.Id == 1);
            if (settings == null)
            {
                settings = new ApplicationSettings { Id = 1 };
                setup.ApplicationSettings.Add(settings);
                await setup.SaveChangesAsync();
                createdSettings = true;
            }
            (originalActive, originalEnabled, originalGeneration, settingsRowVersion) =
                (settings.ActiveRoutingProviderConfigurationId, settings.ExternalRouteGenerationEnabled,
                    settings.ExternalRouteGenerationVersion, settings.RowVersion);
            var profile = await setup.Set<TransportProfile>().FirstOrDefaultAsync();
            if (profile == null)
            {
                profile = new TransportProfile
                {
                    Id = Guid.NewGuid(), Key = $"routing-race-{Guid.NewGuid():N}", Label = "Routing race",
                    Category = "Test", IsActive = true
                };
                setup.Set<TransportProfile>().Add(profile);
                createdProfile = true;
            }
            profileId = profile.Id;
            setup.Set<RoutingProviderConfiguration>().AddRange(Provider(firstId, profileId), Provider(secondId, profileId));
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var firstContext = _fixture.CreateContext();
            await using var secondContext = _fixture.CreateContext();
            var first = await firstContext.Set<RoutingProviderConfiguration>().AsNoTracking().SingleAsync(item => item.Id == firstId);
            var second = await secondContext.Set<RoutingProviderConfiguration>().AsNoTracking().SingleAsync(item => item.Id == secondId);
            var barrier = new VerificationBarrier();
            var firstService = new RoutingProviderActivationService(firstContext, new BarrierVerifier(firstContext, barrier));
            var secondService = new RoutingProviderActivationService(secondContext, new BarrierVerifier(secondContext, barrier));

            var results = await Task.WhenAll(
                firstService.VerifyAndActivateAsync(firstId, 1, first.RowVersion, settingsRowVersion, "admin", CancellationToken.None),
                secondService.VerifyAndActivateAsync(secondId, 1, second.RowVersion, settingsRowVersion, "admin", CancellationToken.None));

            Assert.Single(results, result => result.Succeeded);
            Assert.Single(results, result => !result.Succeeded && result.ErrorCode == "provider-activation-stale");
            await using var verification = _fixture.CreateContext();
            var selected = (await verification.ApplicationSettings.AsNoTracking().SingleAsync(item => item.Id == 1))
                .ActiveRoutingProviderConfigurationId;
            Assert.Contains(selected, new Guid?[] { firstId, secondId });
        }
        finally
        {
            await using var cleanup = _fixture.CreateContext();
            var settings = await cleanup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            settings.ActiveRoutingProviderConfigurationId = originalActive;
            settings.ExternalRouteGenerationEnabled = originalEnabled;
            settings.ExternalRouteGenerationVersion = originalGeneration;
            await cleanup.SaveChangesAsync();
            await cleanup.Set<RoutingProviderConfiguration>().Where(item => item.Id == firstId || item.Id == secondId)
                .ExecuteDeleteAsync();
            if (createdProfile) await cleanup.Set<TransportProfile>().Where(item => item.Id == profileId).ExecuteDeleteAsync();
            if (createdSettings) await cleanup.ApplicationSettings.Where(item => item.Id == 1).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task CredentialClearRacingFeatureEnable_NeverLeavesEnabledProviderWithoutCredential()
    {
        _fixture.RequireAvailable();
        var providerId = Guid.NewGuid();
        Guid? originalActive;
        bool originalEnabled;
        int originalGeneration;
        var credentials = new RoutingProviderCredentialService(new EphemeralDataProtectionProvider());
        uint settingsRowVersion, providerRowVersion;
        await using (var setup = _fixture.CreateContext())
        {
            var settings = await setup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            (originalActive, originalEnabled, originalGeneration) = (settings.ActiveRoutingProviderConfigurationId,
                settings.ExternalRouteGenerationEnabled, settings.ExternalRouteGenerationVersion);
            var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
            var provider = Provider(providerId, profile.Id);
            provider.CredentialRequired = true;
            credentials.Replace(provider, "run-owned-secret");
            provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
            setup.Set<RoutingProviderConfiguration>().Add(provider);
            settings.ActiveRoutingProviderConfigurationId = providerId;
            settings.ExternalRouteGenerationEnabled = false;
            await setup.SaveChangesAsync();
            settingsRowVersion = settings.RowVersion;
            providerRowVersion = provider.RowVersion;
        }

        try
        {
            await using var clearContext = _fixture.CreateContext();
            await using var enableContext = _fixture.CreateContext();
            var clear = new RoutingProviderAdministrationService(clearContext, credentials);
            var enable = new RoutingProviderAdministrationService(enableContext, credentials);
            await Task.WhenAll(
                clear.ClearCredentialAsync(providerId, true, false, providerRowVersion, settingsRowVersion,
                    "admin-clear", CancellationToken.None),
                enable.SetFeatureEnabledAsync(true, settingsRowVersion, "admin-enable", CancellationToken.None));

            await using var verification = _fixture.CreateContext();
            var settings = await verification.ApplicationSettings.AsNoTracking().SingleAsync(item => item.Id == 1);
            var provider = await verification.Set<RoutingProviderConfiguration>().AsNoTracking()
                .SingleAsync(item => item.Id == providerId);
            Assert.False(settings.ExternalRouteGenerationEnabled && (!provider.CredentialPresent
                || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion));
        }
        finally
        {
            await using var cleanup = _fixture.CreateContext();
            var settings = await cleanup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            settings.ActiveRoutingProviderConfigurationId = originalActive;
            settings.ExternalRouteGenerationEnabled = originalEnabled;
            settings.ExternalRouteGenerationVersion = originalGeneration;
            await cleanup.SaveChangesAsync();
            await cleanup.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task ActivationRacingProfileDeactivation_PreservesPreviousSelection()
    {
        _fixture.RequireAvailable();
        var providerId = Guid.NewGuid();
        Guid profileId;
        bool originalProfileActive;
        Guid? originalActive;
        uint settingsRowVersion;
        await using (var setup = _fixture.CreateContext())
        {
            var settings = await setup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            originalActive = settings.ActiveRoutingProviderConfigurationId;
            settingsRowVersion = settings.RowVersion;
            var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
            profileId = profile.Id;
            originalProfileActive = profile.IsActive;
            setup.Set<RoutingProviderConfiguration>().Add(Provider(providerId, profileId));
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var activationContext = _fixture.CreateContext();
            await using var profileContext = _fixture.CreateContext();
            var provider = await activationContext.Set<RoutingProviderConfiguration>().AsNoTracking()
                .SingleAsync(item => item.Id == providerId);
            var service = new RoutingProviderActivationService(activationContext,
                new ProfileDeactivatingVerifier(activationContext, profileContext, profileId));

            var result = await service.VerifyAndActivateAsync(
                providerId, provider.ConfigurationVersion, provider.RowVersion, settingsRowVersion, "admin", CancellationToken.None);

            Assert.False(result.Succeeded);
            await using var verification = _fixture.CreateContext();
            Assert.Equal(originalActive, (await verification.ApplicationSettings.AsNoTracking()
                .SingleAsync(item => item.Id == 1)).ActiveRoutingProviderConfigurationId);
        }
        finally
        {
            await using var cleanup = _fixture.CreateContext();
            var profile = await cleanup.Set<TransportProfile>().SingleAsync(item => item.Id == profileId);
            profile.IsActive = originalProfileActive;
            await cleanup.SaveChangesAsync();
            await cleanup.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId).ExecuteDeleteAsync();
        }
    }

    private static RoutingProviderConfiguration Provider(Guid id, Guid profileId)
    {
        var provider = new RoutingProviderConfiguration
        {
            Id = id, DisplayName = $"Race {id:N}", BaseEndpoint = "https://routing.invalid", Enabled = true,
            VerificationFromLongitude = 1, VerificationFromLatitude = 2,
            VerificationToLongitude = 3, VerificationToLatitude = 4
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = id, TransportProfileId = profileId, OsrmProfile = "driving"
        });
        return provider;
    }

    private sealed class VerificationBarrier
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _release.TrySetResult();
            await _release.Task;
        }
    }

    private sealed class BarrierVerifier(ApplicationDbContext db, VerificationBarrier barrier) : IRoutingProviderVerifier
    {
        public async Task<RoutingVerificationResult> VerifyAsync(
            Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken)
        {
            var provider = await db.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId, cancellationToken);
            provider.VerifiedConfigurationVersion = expectedVersion;
            await db.SaveChangesAsync(cancellationToken);
            await barrier.ArriveAsync();
            return new RoutingVerificationResult(true, null, expectedVersion, provider.RowVersion);
        }
    }

    private sealed class ProfileDeactivatingVerifier(
        ApplicationDbContext providerDb, ApplicationDbContext profileDb, Guid profileId) : IRoutingProviderVerifier
    {
        public async Task<RoutingVerificationResult> VerifyAsync(
            Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken)
        {
            var provider = await providerDb.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId, cancellationToken);
            provider.VerifiedConfigurationVersion = expectedVersion;
            await providerDb.SaveChangesAsync(cancellationToken);
            var profile = await profileDb.Set<TransportProfile>().SingleAsync(item => item.Id == profileId, cancellationToken);
            profile.IsActive = false;
            await profileDb.SaveChangesAsync(cancellationToken);
            return new RoutingVerificationResult(true, null, expectedVersion, provider.RowVersion);
        }
    }
}
