using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves proposal acceptance observes one PostgreSQL authority state.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ExternalRouteProposalAcceptancePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves credential replacement either wins before authority locks or waits behind acceptance.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeoapifyAcceptance_SerializesWithCredentialReplacement(bool acceptanceOwnsAuthorityFirst)
    {
        fixture.RequireAvailable();
        var protection = new EphemeralDataProtectionProvider();
        var seeded = await SeedGeoapifyProposalAsync(protection);
        var gate = new PersonalAuthorityLockGate(acceptanceOwnsAuthorityFirst);
        await using var acceptanceContext = fixture.CreateContext(gate);
        await using var mutationContext = fixture.CreateContext();
        var credentials = new PersonalProviderCredentialService(protection);
        var resolver = new AuthoritativeRoutingProviderResolver(acceptanceContext,
            new RoutingProviderCredentialService(protection), new UserRoutingCredentialService(protection), credentials);
        var service = new ExternalRouteProposalAcceptanceService(
            acceptanceContext, seeded.AggregateTokens, seeded.Contexts, resolver);
        var acceptanceTask = service.AcceptAsync(seeded.UserId, seeded.TripId, seeded.SegmentId,
            seeded.Binding.ProposalId, seeded.Geometry, seeded.Indices, seeded.ProtectedContext, CancellationToken.None);
        await gate.Paused.WaitAsync(TimeSpan.FromSeconds(10));

        var mutationTask = new PersonalProviderSetupService(mutationContext, credentials).ReplaceCredentialAsync(
            seeded.UserId, PersonalLocationProvider.Geoapify, "replacement-secret", CancellationToken.None);
        try
        {
            if (acceptanceOwnsAuthorityFirst)
                Assert.NotSame(mutationTask, await Task.WhenAny(mutationTask, Task.Delay(250)));
            else
                await mutationTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            gate.Release();
        }

        var result = await acceptanceTask.WaitAsync(TimeSpan.FromSeconds(10));
        await mutationTask.WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = fixture.CreateContext();
        var segment = await verify.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        if (acceptanceOwnsAuthorityFirst)
        {
            Assert.True(result.Succeeded);
            Assert.Equal("geoapify", segment.RouteProvider);
            Assert.Equal("walk", segment.RouteMappingMode);
            Assert.Equal("persistent", segment.RouteStorageMode);
            Assert.Equal(seeded.Binding.GeneratedAt, segment.RouteGeneratedAt);
            Assert.Equal(seeded.Binding.Attribution, segment.RouteAttribution);
            Assert.Equal(seeded.Binding.TransportProfileId, segment.RouteTransportProfileId);
            Assert.Contains("Continue", segment.RouteInstructionsJson);
            Assert.NotNull(segment.RouteGeometry);
            Assert.Equal(seeded.Binding.DistanceMetres / 1000d, segment.EstimatedDistanceKm);
            Assert.Equal(TimeSpan.FromSeconds(seeded.Binding.DurationSeconds!.Value), segment.EstimatedDuration);
            Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
        }
        else
        {
            Assert.Equal("route-proposal-stale", result.ErrorCode);
            AssertSegmentRouteUnchanged(segment);
        }
    }

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

    private async Task<GeoapifyProposalSeed> SeedGeoapifyProposalAsync(IDataProtectionProvider protection)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var aggregateTokens = new SegmentAggregateTokenService(protection);
        var contexts = new ExternalRouteProposalContextService(protection);
        await using var setup = fixture.CreateContext();
        var segment = await setup.Segments
            .Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(item => item.Place)
            .SingleAsync(item => item.Id == seed.SegmentId);
        segment.EstimatedDistanceKm = null;
        segment.EstimatedDuration = null;
        var profile = PersonalLocationProviderProfile.Create(seed.UserId, PersonalLocationProvider.Geoapify);
        var credentials = new PersonalProviderCredentialService(protection);
        credentials.Replace(profile, "current-secret");
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        var selection = PersonalLocationProviderSelection.Create(seed.UserId);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        setup.AddRange(profile, selection);
        await setup.SaveChangesAsync();
        var places = new[] { segment.FromPlace }
            .Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
            .Concat([segment.ToPlace]).ToArray();
        var geometry = places.Select(item => new RouteCoordinate(item!.Location!.X, item.Location.Y)).ToArray();
        var indices = Enumerable.Range(0, geometry.Length).ToArray();
        var binding = new ExternalRouteProposalBinding(Guid.NewGuid(), seed.TripId, segment.Id, seed.UserId,
            ExternalRouteProposalContextService.GeometryHash(geometry, indices),
            ExternalRouteAnchorFingerprint.Compute(places, geometry), segment.TransportProfileId!.Value,
            Guid.Parse("5bde15a4-984c-4daa-912d-9fa59a166ec3"), 1, ProviderDirectionsCatalog.AuthorityVersion,
            aggregateTokens.Issue(seed.UserId, seed.TripId, segment.Id, segment.RowVersion),
            RoutingProviderSelectionMode.Personal, profile.RoutingGeneration, 1234, 300,
            [new RouteInstruction("Continue", "straight", 0, 3, 1234, 300)], "geoapify", "walk",
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            "Powered by Geoapify|© OpenStreetMap contributors", "persistent");
        return new(seed.UserId, seed.TripId, segment.Id, geometry, indices, binding,
            contexts.Issue(binding).Token, aggregateTokens, contexts);
    }

    private static void AssertSegmentRouteUnchanged(Segment segment)
    {
        Assert.Null(segment.RouteGeometry);
        Assert.Null(segment.EstimatedDistanceKm);
        Assert.Null(segment.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Manual, segment.EstimatedDurationSource);
        Assert.Null(segment.RouteInstructionsJson);
        Assert.Null(segment.RouteProvider);
        Assert.Null(segment.RouteProviderConfigurationId);
        Assert.Null(segment.RouteProviderConfigurationVersion);
        Assert.Null(segment.RouteTransportProfileId);
        Assert.Null(segment.RouteMappingMode);
        Assert.Null(segment.RouteGeneratedAt);
        Assert.Null(segment.RouteAttribution);
        Assert.Null(segment.RouteStorageMode);
    }

    private sealed record GeoapifyProposalSeed(
        string UserId, Guid TripId, Guid SegmentId, RouteCoordinate[] Geometry, int[] Indices,
        ExternalRouteProposalBinding Binding, string ProtectedContext, SegmentAggregateTokenService AggregateTokens,
        ExternalRouteProposalContextService Contexts);

    private sealed class PersonalAuthorityLockGate(bool pauseAfterProfileLock) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Paused => _paused.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!pauseAfterProfileLock && IsLock(command, "PersonalLocationProviderSelections"))
                await PauseAsync(cancellationToken);
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (pauseAfterProfileLock && IsLock(command, "PersonalLocationProviderProfiles"))
                await PauseAsync(cancellationToken);
            return result;
        }

        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            if (_paused.Task.IsCompleted) return;
            _paused.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        private static bool IsLock(DbCommand command, string table) =>
            command.CommandText.Contains(table, StringComparison.Ordinal)
            && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
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
