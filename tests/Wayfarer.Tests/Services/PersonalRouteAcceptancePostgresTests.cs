using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using System.Data.Common;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves personal routing authority is serialized with proposal acceptance in PostgreSQL.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class PersonalRouteAcceptancePostgresTests(PostgresMigrationTestFixture fixture)
{
    [PostgresFact]
    public async Task SelectionMutationCommitsFirstAndConcurrentAcceptanceRejectsWithoutChangingSegment()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new PersonalProviderCredentialService(protection);
        var seeded = await SeedAsync(user.Id, credentials);
        var proposalContexts = new ExternalRouteProposalContextService(protection);
        var aggregateTokens = new SegmentAggregateTokenService(protection);
        var aggregateToken = aggregateTokens.Issue(user.Id, seeded.TripId, seeded.SegmentId, seeded.RowVersion);
        RouteCoordinate[] geometry = [new(23.7, 37.9), new(23.8, 38.0)];
        int[] indices = [0, 1];
        var proposalId = Guid.NewGuid();
        var binding = new ExternalRouteProposalBinding(
            proposalId, seeded.TripId, seeded.SegmentId, user.Id,
            ExternalRouteProposalContextService.GeometryHash(geometry, indices), seeded.AnchorFingerprint,
            seeded.TransportProfileId, aggregateToken, 1000, 300,
            [new("Continue", "straight", 0, 1, 1000, 300)], "geoapify", "drive",
            DateTimeOffset.Parse("2026-09-04T09:00:00Z"), "Geoapify attribution", "persistent",
            seeded.SelectionGeneration, seeded.CredentialGeneration, seeded.RoutingGeneration,
            ProviderDirectionsCatalog.AuthorityVersion);
        var token = proposalContexts.Issue(binding).Token;

        await using var mutationContext = fixture.CreateContext();
        await using var mutation = await mutationContext.Database.BeginTransactionAsync();
        var selection = await mutationContext.PersonalLocationProviderSelections.FromSqlInterpolated($$"""
            SELECT *, xmin FROM "PersonalLocationProviderSelections"
            WHERE "UserId" = {{user.Id}} FOR UPDATE
            """).SingleAsync();
        selection.Select(PersonalProviderCapability.Routing, null);
        await mutationContext.SaveChangesAsync();

        var acceptanceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var acceptanceContext = fixture.CreateContext(new SelectionLockObserver(acceptanceStarted));
        var acceptance = new ExternalRouteProposalAcceptanceService(acceptanceContext, aggregateTokens,
            proposalContexts, new AuthoritativeRoutingProviderResolver(acceptanceContext, credentials));
        var acceptanceTask = acceptance.AcceptAsync(user.Id, seeded.TripId, seeded.SegmentId, proposalId,
            geometry, indices, token, CancellationToken.None);
        await acceptanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await mutation.CommitAsync();

        var result = await acceptanceTask;

        Assert.False(result.Succeeded);
        Assert.Equal("route-proposal-stale", result.ErrorCode);
        await using var verification = fixture.CreateContext();
        var persistedSelection = await verification.PersonalLocationProviderSelections.AsNoTracking()
            .SingleAsync(item => item.UserId == user.Id);
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(persistedSelection.RoutingProviderKey);
        Assert.Equal(seeded.SelectionGeneration + 1, persistedSelection.RoutingSelectionGeneration);
        Assert.Null(segment.RouteGeometry);
        Assert.Null(segment.RouteProvider);
        Assert.Null(segment.RouteTransportProfileId);
    }

    private async Task<SeededAuthority> SeedAsync(string userId, PersonalProviderCredentialService credentials)
    {
        await using var context = fixture.CreateContext();
        var providerProfile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
        credentials.Replace(providerProfile, "race-credential");
        providerProfile.SetAuthorization(PersonalProviderCapability.Routing, true);
        credentials.RecordVerification(providerProfile, PersonalProviderCapability.Routing,
            PersonalProviderVerification.Verified);
        var selection = PersonalLocationProviderSelection.Create(userId);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        var transportProfile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = $"race-{Guid.NewGuid():N}", Label = "Race planning",
            Category = "test", IsActive = true
        };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Acceptance race" };
        var region = new Region
        {
            Id = Guid.NewGuid(), UserId = userId, Trip = trip, TripId = trip.Id, Name = "Race region"
        };
        var from = CreatePlace(userId, region, 23.7, 37.9);
        var to = CreatePlace(userId, region, 23.8, 38.0);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), UserId = userId, Trip = trip, TripId = trip.Id,
            Mode = transportProfile.Key, TransportProfile = transportProfile, TransportProfileId = transportProfile.Id,
            FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id
        };
        context.AddRange(providerProfile, selection, transportProfile, trip, region, from, to, segment);
        await context.SaveChangesAsync();
        var fingerprint = ExternalRouteAnchorFingerprint.Compute([from, to],
            [new RouteCoordinate(23.7, 37.9), new RouteCoordinate(23.8, 38.0)]);
        return new(trip.Id, segment.Id, transportProfile.Id, segment.RowVersion, fingerprint,
            selection.RoutingSelectionGeneration, providerProfile.CredentialGeneration, providerProfile.RoutingGeneration);
    }

    private static Place CreatePlace(string userId, Region region, double longitude, double latitude) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Region = region, RegionId = region.Id, Name = "Anchor",
        Location = new Point(longitude, latitude) { SRID = 4326 }
    };

    private sealed record SeededAuthority(
        Guid TripId, Guid SegmentId, Guid TransportProfileId, uint RowVersion, string AnchorFingerprint,
        int SelectionGeneration, int CredentialGeneration, int RoutingGeneration);

    private sealed class SelectionLockObserver(TaskCompletionSource started) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
