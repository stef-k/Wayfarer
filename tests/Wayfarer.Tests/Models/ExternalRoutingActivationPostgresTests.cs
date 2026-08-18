using Microsoft.EntityFrameworkCore;
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
                firstService.VerifyAndActivateAsync(firstId, 1, first.RowVersion, settingsRowVersion, CancellationToken.None),
                secondService.VerifyAndActivateAsync(secondId, 1, second.RowVersion, settingsRowVersion, CancellationToken.None));

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
            Guid providerId, int expectedVersion, uint expectedRowVersion, CancellationToken cancellationToken)
        {
            var provider = await db.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId, cancellationToken);
            provider.VerifiedConfigurationVersion = expectedVersion;
            await db.SaveChangesAsync(cancellationToken);
            await barrier.ArriveAsync();
            return new RoutingVerificationResult(true, null, expectedVersion, provider.RowVersion);
        }
    }
}
