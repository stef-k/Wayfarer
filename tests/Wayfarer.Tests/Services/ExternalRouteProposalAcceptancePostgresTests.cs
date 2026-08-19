using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves proposal acceptance observes one PostgreSQL authority state.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ExternalRouteProposalAcceptancePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves acceptance cannot combine pre-change settings with post-change provider state.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_RacingAuthorityChange_LinearizesBeforeChangeOrRejectsAfterward(bool disableFeature)
    {
        fixture.RequireAvailable();
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var firstProviderId = Guid.NewGuid();
        var secondProviderId = Guid.NewGuid();
        Guid? originalActive;
        bool originalEnabled;
        int originalGeneration;
        bool createdSettings = false;
        var protection = new EphemeralDataProtectionProvider();
        var aggregateTokens = new SegmentAggregateTokenService(protection);
        var contexts = new ExternalRouteProposalContextService(protection);
        ExternalRouteProposalBinding binding;
        string protectedContext;
        RouteCoordinate[] geometry;
        int[] indices;

        await using (var setup = fixture.CreateContext())
        {
            var settings = await setup.ApplicationSettings.SingleOrDefaultAsync(item => item.Id == 1);
            if (settings == null)
            {
                settings = new ApplicationSettings { Id = 1 };
                setup.ApplicationSettings.Add(settings);
                createdSettings = true;
            }
            (originalActive, originalEnabled, originalGeneration) =
                (settings.ActiveRoutingProviderConfigurationId, settings.ExternalRouteGenerationEnabled,
                    settings.ExternalRouteGenerationVersion);
            var segment = await setup.Segments.AsNoTracking()
                .Include(item => item.FromPlace).Include(item => item.ToPlace)
                .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(item => item.Place)
                .SingleAsync(item => item.Id == seed.SegmentId);
            var places = new[] { segment.FromPlace }
                .Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
                .Concat([segment.ToPlace]).ToArray();
            geometry = places.Select(item => new RouteCoordinate(item!.Location!.X, item.Location.Y)).ToArray();
            indices = Enumerable.Range(0, geometry.Length).ToArray();
            var firstProvider = Provider(firstProviderId, seed.FirstProfileId);
            var secondProvider = Provider(secondProviderId, seed.FirstProfileId);
            setup.Set<RoutingProviderConfiguration>().AddRange(firstProvider, secondProvider);
            settings.ExternalRouteGenerationEnabled = true;
            settings.ExternalRouteGenerationVersion = originalGeneration + 1;
            settings.ActiveRoutingProviderConfigurationId = firstProviderId;
            await setup.SaveChangesAsync();
            var aggregateToken = aggregateTokens.Issue(seed.UserId, seed.TripId, segment.Id, segment.RowVersion);
            binding = new ExternalRouteProposalBinding(
                Guid.NewGuid(), seed.TripId, segment.Id, seed.UserId,
                ExternalRouteProposalContextService.GeometryHash(geometry, indices),
                ExternalRouteAnchorFingerprint.Compute(places, geometry), segment.TransportProfileId!.Value,
                firstProviderId, firstProvider.ConfigurationVersion, settings.ExternalRouteGenerationVersion,
                aggregateToken);
            protectedContext = contexts.Issue(binding).Token;
        }

        try
        {
            var gate = new SettingsReadGate();
            await using var acceptanceContext = fixture.CreateContext(gate);
            await using var authorityContext = fixture.CreateContext();
            var resolver = new AuthoritativeRoutingProviderResolver(acceptanceContext,
                new RoutingProviderCredentialService(protection), new UserRoutingCredentialService(protection));
            var service = new ExternalRouteProposalAcceptanceService(acceptanceContext, aggregateTokens, contexts, resolver);
            var acceptanceTask = service.AcceptAsync(seed.UserId, seed.TripId, seed.SegmentId!.Value,
                binding.ProposalId, geometry, indices, protectedContext, CancellationToken.None);
            await gate.SettingsRead.WaitAsync(TimeSpan.FromSeconds(10));
            var authorityTask = disableFeature
                ? authorityContext.ApplicationSettings.Where(item => item.Id == 1)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(item => item.ExternalRouteGenerationEnabled, false)
                        .SetProperty(item => item.ExternalRouteGenerationVersion, item => item.ExternalRouteGenerationVersion + 1))
                : authorityContext.ApplicationSettings.Where(item => item.Id == 1)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(item => item.ActiveRoutingProviderConfigurationId, secondProviderId)
                        .SetProperty(item => item.ExternalRouteGenerationVersion, item => item.ExternalRouteGenerationVersion + 1));
            var authorityChangedFirst = await Task.WhenAny(authorityTask, Task.Delay(250)) == authorityTask;
            gate.Release();
            var result = await acceptanceTask.WaitAsync(TimeSpan.FromSeconds(10));
            await authorityTask.WaitAsync(TimeSpan.FromSeconds(10));

            if (authorityChangedFirst)
                Assert.Equal("route-proposal-stale", result.ErrorCode);
            else
                Assert.True(result.Succeeded);
            Assert.False(acceptanceContext.ChangeTracker.HasChanges());
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            var settings = await cleanup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            settings.ActiveRoutingProviderConfigurationId = originalActive;
            settings.ExternalRouteGenerationEnabled = originalEnabled;
            settings.ExternalRouteGenerationVersion = originalGeneration;
            await cleanup.SaveChangesAsync();
            await cleanup.Set<RoutingProviderConfiguration>()
                .Where(item => item.Id == firstProviderId || item.Id == secondProviderId).ExecuteDeleteAsync();
            if (createdSettings) await cleanup.ApplicationSettings.Where(item => item.Id == 1).ExecuteDeleteAsync();
        }
    }

    private static RoutingProviderConfiguration Provider(Guid id, Guid profileId)
    {
        var provider = new RoutingProviderConfiguration
        {
            Id = id, DisplayName = $"Acceptance {id:N}", BaseEndpoint = "https://routing.invalid",
            Enabled = true, ConfigurationVersion = 3, VerifiedConfigurationVersion = 3,
            VerificationFromLongitude = 1, VerificationFromLatitude = 2,
            VerificationToLongitude = 3, VerificationToLatitude = 4
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = id, TransportProfileId = profileId, OsrmProfile = "walking"
        });
        return provider;
    }

    private sealed class SettingsReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _settingsRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SettingsRead => _settingsRead.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await PauseAfterSettingsReadAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            await PauseAfterSettingsReadAsync(command, cancellationToken);
            return result;
        }

        private async Task PauseAfterSettingsReadAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("ApplicationSettings", StringComparison.Ordinal)
                || _settingsRead.Task.IsCompleted) return;
            _settingsRead.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
