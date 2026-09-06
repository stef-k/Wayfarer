using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;
using System.Data.Common;
using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;
using System.Runtime.ExceptionServices;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves personal routing authority is serialized with proposal Save in PostgreSQL.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class PersonalRouteSavePostgresTests(PostgresMigrationTestFixture fixture)
{
    [PostgresFact(Timeout = 30_000)]
    public async Task SelectionMutationCommitsFirstAndConcurrentSaveRejectsWithoutChangingSegment()
    {
        var operationTimeout = TimeSpan.FromSeconds(10);
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new PersonalProviderCredentialService(protection);
        var seeded = await SeedAsync(user.Id, credentials);
        var proposalContexts = new ExternalRouteProposalContextService(protection);
        var aggregateTokens = new SegmentAggregateTokenService(protection);
        var aggregateToken = aggregateTokens.Issue(user.Id, seeded.TripId, seeded.SegmentId, seeded.RowVersion);
        var proposal = await GenerateAsync(user.Id, seeded, credentials, protection);

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
        var save = SaveService(acceptanceContext, protection, credentials);
        using var acceptanceCancellation = new CancellationTokenSource();
        using var body = await BodyAsync(seeded.SegmentId, aggregateToken, proposal);
        var acceptanceTask = save.UpdateSegmentAsync(seeded.TripId, seeded.SegmentId, user.Id,
            body, null, acceptanceCancellation.Token);
        Exception? primary = null;
        try
        {
            await acceptanceStarted.Task.WaitAsync(operationTimeout);
            await mutation.CommitAsync();

            var result = await acceptanceTask.WaitAsync(operationTimeout);
            Assert.NotEqual(EditorRegionMutationStatus.Success, result.Status);
            Assert.True(result.Code == "route-proposal-stale" || result.Status == EditorRegionMutationStatus.Conflict);
            await using var verification = fixture.CreateContext();
            var persistedSelection = await verification.PersonalLocationProviderSelections.AsNoTracking()
                .SingleAsync(item => item.UserId == user.Id);
            var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
            Assert.Null(persistedSelection.RoutingProviderKey);
            Assert.Equal(seeded.SelectionGeneration + 1, persistedSelection.RoutingSelectionGeneration);
            Assert.Equal(seeded.RowVersion, segment.RowVersion);
            Assert.Equal("original", segment.Notes);
            Assert.Null(segment.RouteGeometry);
            Assert.Null(segment.RouteProvider);
            Assert.Null(segment.RouteTransportProfileId);
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            if (primary is not null)
            {
                acceptanceCancellation.Cancel();
                try
                {
                    if (mutation.GetDbTransaction().Connection is not null)
                        await mutation.RollbackAsync(CancellationToken.None);
                }
                catch (Exception) { }

                if (acceptanceTask.IsCompleted)
                {
                    try { await acceptanceTask; }
                    catch (Exception) { }
                }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Preserves inactive planning identity, including a Manual edit with no provider duration.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateThenSaveCommitsProposalAndNotesAndRetainsTrustedRouteOnFollowup(bool manualEdit)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new PersonalProviderCredentialService(protection);
        var seeded = await SeedAsync(user.Id, credentials, inactiveChoice: true);
        var proposal = await GenerateAsync(user.Id, seeded, credentials, protection, manualEdit ? null : 360);
        await using var context = fixture.CreateContext();
        var original = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(seeded.RowVersion, original.RowVersion);
        Assert.Null(original.RouteGeometry);
        Assert.Equal("original", original.Notes);
        Assert.Equal(7, original.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromMinutes(8), original.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, original.EstimatedDurationSource);
        var service = SaveService(context, protection, credentials);
        using var body = await BodyAsync(seeded.SegmentId, service.IssueAggregateToken(user.Id, seeded.TripId, original), proposal, manualEdit);
        var saved = await service.UpdateSegmentAsync(seeded.TripId, seeded.SegmentId, user.Id, body, null, CancellationToken.None);
        Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
        context.ChangeTracker.Clear();
        var canonical = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal("edited with proposal", canonical.Notes);
        Assert.Equal(original.Mode, canonical.Mode);
        Assert.Equal(original.TransportProfileId, canonical.TransportProfileId);
        Assert.False(await context.Set<TransportProfile>().Where(item => item.Id == seeded.TransportProfileId).Select(item => item.IsActive).SingleAsync());
        Assert.Equal(proposal.Geometry.Select(item => new Coordinate(item.Longitude, item.Latitude)), canonical.RouteGeometry!.Coordinates);
        Assert.Equal(1.25, canonical.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromMinutes(manualEdit ? 12 : 6), canonical.EstimatedDuration);
        Assert.Equal(manualEdit ? EstimatedDurationSource.Manual : EstimatedDurationSource.Automatic, canonical.EstimatedDurationSource);
        Assert.Equal("geoapify", canonical.RouteProvider);
        Assert.Equal("drive", canonical.RouteMappingMode);
        Assert.Equal(seeded.TransportProfileId, canonical.RouteTransportProfileId);
        Assert.Equal("persistent", canonical.RouteStorageMode);
        Assert.NotNull(canonical.RouteGeneratedAt);
        Assert.NotNull(canonical.RouteAttribution);
        Assert.Null(canonical.RouteProviderConfigurationId);
        Assert.Null(canonical.RouteProviderConfigurationVersion);
        Assert.Equal(proposal.Instructions, JsonSerializer.Deserialize<RouteInstruction[]>(canonical.RouteInstructionsJson!));
        Assert.Equal(1, await context.Set<SegmentWaypoint>().Where(item => item.SegmentId == canonical.Id).Select(item => item.RouteVertexIndex).SingleAsync());
        var selection = await context.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == user.Id);
        selection.Select(PersonalProviderCapability.Routing, null);
        await context.SaveChangesAsync();
        using var followup = await BodyAsync(seeded.SegmentId, saved.Result!.Data.AggregateConcurrencyToken, null, manualEdit);
        var again = await service.UpdateSegmentAsync(seeded.TripId, seeded.SegmentId, user.Id, followup, null, CancellationToken.None);
        Assert.Equal(EditorRegionMutationStatus.Success, again.Status);
        context.ChangeTracker.Clear();
        var retained = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(canonical.EstimatedDistanceKm, retained.EstimatedDistanceKm);
        Assert.Equal(canonical.EstimatedDuration, retained.EstimatedDuration);
        Assert.Equal(canonical.EstimatedDurationSource, retained.EstimatedDurationSource);
        Assert.Equal(canonical.RouteInstructionsJson, retained.RouteInstructionsJson);
        Assert.Equal(canonical.RouteProvider, retained.RouteProvider);
        Assert.Equal(canonical.RouteAttribution, retained.RouteAttribution);
        Assert.Equal(canonical.RouteGeneratedAt, retained.RouteGeneratedAt);
        Assert.Equal(canonical.RouteTransportProfileId, retained.RouteTransportProfileId);
    }

    /// <summary>Uses the real generator with a controlled provider boundary and no network contact.</summary>
    private async Task<ExternalRouteProposalDto> GenerateAsync(
        string userId, SeededAuthority seeded, PersonalProviderCredentialService credentials, IDataProtectionProvider protection,
        double? durationSeconds = 360)
    {
        await using var context = fixture.CreateContext();
        var tokens = new SegmentAggregateTokenService(protection);
        var generator = new ExternalRouteProposalGenerator(context, tokens, new ControlledRouteClient(durationSeconds),
            new ControlledGeometryValidator(), new ExternalRouteProposalContextService(protection), new RoutingRequestBudget(),
            new AuthoritativeRoutingProviderResolver(context, credentials));
        var generated = await generator.GenerateAsync(userId, seeded.TripId, seeded.SegmentId,
            tokens.Issue(userId, seeded.TripId, seeded.SegmentId, seeded.RowVersion), "drive", CancellationToken.None);
        Assert.True(generated.Succeeded, generated.ErrorCode);
        return generated.Proposal!;
    }

    private static TripEditorSegmentMutationService SaveService(ApplicationDbContext context,
        IDataProtectionProvider protection, PersonalProviderCredentialService credentials) => new(context,
            new SegmentAggregateTokenService(protection), new SegmentRouteClearConfirmation(protection, TimeProvider.System),
            new ExternalRouteProposalSaveValidator(context, new SegmentAggregateTokenService(protection),
                new ExternalRouteProposalContextService(protection), new AuthoritativeRoutingProviderResolver(context, credentials)));

    /// <summary>Submits effective route fields and the original protected identity through ordinary Save JSON.</summary>
    private async Task<Stream> BodyAsync(Guid segmentId, string token, ExternalRouteProposalDto? proposal, bool manualEdit = false)
    {
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.Include(item => item.Waypoints).AsNoTracking().SingleAsync(item => item.Id == segmentId);
        var coordinates = proposal?.Geometry.Select(item => new[] { item.Longitude, item.Latitude }).ToArray()
            ?? segment.RouteGeometry?.Coordinates.Select(item => new[] { item.X, item.Y }).ToArray();
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new {
            fromPlaceId = segment.FromPlaceId, toPlaceId = segment.ToPlaceId,
            waypointPlaceIds = segment.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId),
            waypointRouteVertexIndices = proposal?.WaypointIndices.Skip(1).SkipLast(1).Select(item => (int?)item).ToArray()
                ?? segment.Waypoints.OrderBy(item => item.Position).Select(item => item.RouteVertexIndex).ToArray(),
            mode = segment.Mode, estimatedDistanceKm = manualEdit ? proposal?.DistanceMetres / 1000 ?? segment.EstimatedDistanceKm : 999,
            estimatedDurationMinutes = manualEdit ? 12 : 999,
            estimatedDurationSource = manualEdit ? "Manual" : "Automatic", notesHtml = "edited with proposal",
            route = coordinates == null ? null : new { type = "LineString", coordinates }, aggregateConcurrencyToken = token,
            proposal = proposal == null ? null : new { proposal.ProposalId, proposal.ProtectedContext, manualDurationOverride = manualEdit }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed class ControlledRouteClient(double? durationSeconds) : IProviderRouteClient
    {
        public Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderRouteResult(true, anchors, anchors, null, 1250, durationSeconds,
                [new("Continue", "straight", 0, anchors.Count - 1, 1250, 360)], Enumerable.Range(0, anchors.Count).ToArray()));
    }

    private sealed class ControlledGeometryValidator : IProviderRouteGeometryValidator
    {
        public ProviderRouteValidationResult Validate(IReadOnlyList<RouteCoordinate> anchors,
            ProviderRouteResult providerRoute, CancellationToken cancellationToken) =>
            new(true, providerRoute.Geometry, Enumerable.Range(0, anchors.Count).ToArray(), null);
    }

    private async Task<SeededAuthority> SeedAsync(string userId, PersonalProviderCredentialService credentials,
        bool inactiveChoice = false)
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
            Id = Guid.NewGuid(), Key = $"race-{Guid.NewGuid():N}", Label = "Fish",
            Category = "test", IsActive = !inactiveChoice, PlanningSpeedKmh = 5
        };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Acceptance race" };
        var region = new Region
        {
            Id = Guid.NewGuid(), UserId = userId, Trip = trip, TripId = trip.Id, Name = "Race region"
        };
        var from = CreatePlace(userId, region, 23.7, 37.9);
        var via = CreatePlace(userId, region, 23.75, 37.95);
        var to = CreatePlace(userId, region, 23.8, 38.0);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), UserId = userId, Trip = trip, TripId = trip.Id,
            Mode = transportProfile.Key, TransportProfile = transportProfile, TransportProfileId = transportProfile.Id,
            Notes = "original", EstimatedDistanceKm = 7, EstimatedDuration = TimeSpan.FromMinutes(8),
            FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id
        };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id,
            Place = via, PlaceId = via.Id, Position = 0 });
        context.AddRange(providerProfile, selection, transportProfile, trip, region, from, via, to, segment);
        await context.SaveChangesAsync();
        var fingerprint = ExternalRouteAnchorFingerprint.Compute([from, via, to],
            [new RouteCoordinate(23.7, 37.9), new RouteCoordinate(23.75, 37.95), new RouteCoordinate(23.8, 38.0)]);
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
