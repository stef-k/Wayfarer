using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes waypoint migration, constraint, relationship, and transaction behavior on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentWaypointPostgresTests
{
    private const string PreviousMigration = "20260728152323_AdminManagedTransportProfiles";
    private readonly PostgresImportTestFixture _fixture;

    /// <summary>Initializes provider tests over the guarded isolated database fixture.</summary>
    public SegmentWaypointPostgresTests(PostgresImportTestFixture fixture) => _fixture = fixture;

    /// <summary>Proves real PostgreSQL checks, unique constraints, filtered indexes, and both FK policies.</summary>
    [PostgresFact]
    public async Task ProviderConstraints_EnforceRangesUniquenessCascadeAndRestriction()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedAggregateAsync();
        await using var context = _fixture.CreateContext();

        await AssertInsertFailsAsync(context, aggregate.SegmentId, aggregate.FirstPlaceId, 1, null);
        await AssertInsertFailsAsync(context, Guid.NewGuid(), aggregate.SecondPlaceId, 1, null);
        await AssertInsertFailsAsync(context, aggregate.SegmentId, Guid.NewGuid(), 1, null);
        await AssertInsertFailsAsync(context, aggregate.SegmentId, aggregate.SecondPlaceId, -1, null);
        await AssertInsertFailsAsync(context, aggregate.SegmentId, aggregate.SecondPlaceId, 1, 0);
        await AssertInsertFailsAsync(context, aggregate.SegmentId, aggregate.SecondPlaceId, 0, null);
        await AssertInsertFailsAsync(context, aggregate.SegmentId, aggregate.SecondPlaceId, 1, 2);

        // PostgreSQL's partial index permits multiple null mappings while still rejecting duplicate custom indices.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."SegmentWaypoints" ("SegmentId", "PlaceId", "Position", "RouteVertexIndex") VALUES ({aggregate.SegmentId}, {aggregate.SecondPlaceId}, {1}, {null as int?})""");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM public."SegmentWaypoints" WHERE "SegmentId" = {aggregate.SegmentId} AND "PlaceId" = {aggregate.SecondPlaceId}""");

        await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM public."Places" WHERE "Id" = {aggregate.FirstPlaceId}"""));
        Assert.True(await context.Segments.AsNoTracking().AnyAsync(item => item.Id == aggregate.SegmentId));

        await context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM public."Segments" WHERE "Id" = {aggregate.SegmentId}""");
        Assert.False(await context.Set<SegmentWaypoint>().AsNoTracking().AnyAsync(item => item.SegmentId == aggregate.SegmentId));
    }

    /// <summary>Persists representative fallback, custom, and canonical closed-loop aggregates through the reconciler.</summary>
    [PostgresFact]
    public async Task ValidAggregates_PersistFallbackCustomAndClosedLoopMappings()
    {
        _fixture.RequireAvailable();
        var seeded = await SeedPlacesAsync();
        await using var context = _fixture.CreateContext();
        var places = await context.Places.Include(place => place.Region).Where(place => seeded.PlaceIds.Contains(place.Id)).ToDictionaryAsync(place => place.Id);

        var fallback = NewSegment(seeded, 1);
        var custom = NewSegment(seeded, 2);
        var loop = NewSegment(seeded, 3);
        context.Segments.AddRange(fallback, custom, loop);
        await context.SaveChangesAsync();
        Assert.True((await SegmentRouteReconciler.ReconcileAsync(context,
            new(fallback.Id, seeded.PlaceIds[0], seeded.PlaceIds[2], [new(seeded.PlaceIds[1], 0, null)], null))).Succeeded);
        Assert.True((await SegmentRouteReconciler.ReconcileAsync(context,
            new(custom.Id, seeded.PlaceIds[0], seeded.PlaceIds[3],
                [new(seeded.PlaceIds[1], 0, 1), new(seeded.PlaceIds[2], 1, 3)],
                Line((1, 1), (2, 2), (2.5, 2.5), (3, 3), (4, 4))))).Succeeded);
        Assert.True((await SegmentRouteReconciler.ReconcileAsync(context,
            new(loop.Id, seeded.PlaceIds[0], seeded.PlaceIds[0], [new(seeded.PlaceIds[1], 0, null)], null))).Succeeded);
        Assert.Equal(4, await context.Set<SegmentWaypoint>().CountAsync(item => item.SegmentId == fallback.Id || item.SegmentId == custom.Id || item.SegmentId == loop.Id));
        Assert.Equal([1, 3], await context.Set<SegmentWaypoint>().Where(item => item.SegmentId == custom.Id).OrderBy(item => item.Position).Select(item => item.RouteVertexIndex!.Value).ToListAsync());
    }

    /// <summary>Proves rejection inside a real transaction leaves the stored and tracked aggregate unchanged.</summary>
    [PostgresFact]
    public async Task InvalidReconciliation_DoesNotPartiallyPersistOrMutateTrackedAggregate()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var segment = await context.Segments
            .Include(item => item.FromPlace).ThenInclude(place => place!.Region)
            .Include(item => item.ToPlace).ThenInclude(place => place!.Region)
            .Include(item => item.Waypoints).ThenInclude(waypoint => waypoint.Place).ThenInclude(place => place.Region)
            .SingleAsync(item => item.Id == aggregate.SegmentId);
        var originalWaypointId = Assert.Single(segment.Waypoints).PlaceId;
        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, segment.FromPlaceId, segment.ToPlaceId, [new(aggregate.ForeignPlaceId, 0, null)], null));

        Assert.False(result.Succeeded);
        Assert.Equal(originalWaypointId, Assert.Single(segment.Waypoints).PlaceId);
        await using var verification = _fixture.CreateContext();
        Assert.Equal(originalWaypointId, await verification.Set<SegmentWaypoint>().Where(item => item.SegmentId == segment.Id).Select(item => item.PlaceId).SingleAsync());
    }

    /// <summary>Proves the aggregate loader retrieves ordered waypoint places and supports one-unit-of-work updates.</summary>
    [PostgresFact]
    public async Task AggregateLoader_LoadsOrderedGraph_AndPersistsAcceptedUpdateOnce()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var segment = await SegmentRouteReconciler.LoadAggregateAsync(context, aggregate.SegmentId);
        Assert.NotNull(segment);
        Assert.NotNull(segment!.FromPlace?.Region);
        Assert.NotNull(segment.ToPlace?.Region);
        Assert.NotNull(Assert.Single(segment.Waypoints).Place.Region);
        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, segment.FromPlaceId, segment.ToPlaceId, [new(aggregate.SecondPlaceId, 0, null)], null));

        Assert.True(result.Succeeded);
        await using var verification = _fixture.CreateContext();
        var stored = await verification.Set<SegmentWaypoint>().SingleAsync(item => item.SegmentId == segment.Id);
        Assert.Equal(aggregate.SecondPlaceId, stored.PlaceId);
        Assert.Null(stored.RouteVertexIndex);
        Assert.Null(await verification.Segments.Where(item => item.Id == segment.Id).Select(item => item.RouteGeometry).SingleAsync());
    }

    /// <summary>Reordering persisted rows must not collide with PostgreSQL's immediate position uniqueness check.</summary>
    [PostgresFact]
    public async Task Reconcile_PersistedSwap_SucceedsWithoutIntermediatePositionCollision()
    {
        _fixture.RequireAvailable();
        var seeded = await SeedPlacesAsync();
        var segment = NewSegment(seeded, 1);
        await using (var seedContext = _fixture.CreateContext())
        {
            var places = await seedContext.Places.Include(place => place.Region)
                .Where(place => seeded.PlaceIds.Contains(place.Id))
                .ToDictionaryAsync(place => place.Id);
            seedContext.Segments.Add(segment);
            await seedContext.SaveChangesAsync();
            Assert.True((await SegmentRouteReconciler.ReconcileAsync(seedContext,
                new(segment.Id, seeded.PlaceIds[0], seeded.PlaceIds[3],
                    [new(seeded.PlaceIds[1], 0, null), new(seeded.PlaceIds[2], 1, null)], null))).Succeeded);
        }

        await using var context = _fixture.CreateContext();
        var aggregate = await SegmentRouteReconciler.LoadAggregateAsync(context, segment.Id);
        Assert.NotNull(aggregate);
        var original = aggregate!.Waypoints.OrderBy(item => item.Position).ToArray();

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.Id, aggregate.FromPlaceId, aggregate.ToPlaceId,
                [new(original[1].PlaceId, 0, null), new(original[0].PlaceId, 1, null)], null));

        Assert.True(result.Succeeded);
        await using var verification = _fixture.CreateContext();
        Assert.Equal([original[1].PlaceId, original[0].PlaceId],
            await verification.Set<SegmentWaypoint>().Where(item => item.SegmentId == segment.Id)
                .OrderBy(item => item.Position).Select(item => item.PlaceId).ToListAsync());
    }

    /// <summary>Arbitrary persisted reorder shapes replace the complete association set atomically.</summary>
    [PostgresTheory]
    [InlineData(3, 2, 1, 0)]
    [InlineData(1, 2, 3, 0)]
    public async Task Reconcile_PersistedMultiWaypointReorder_CommitsContiguousOrder(params int[] order)
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var proposal = order.Select((sourceIndex, position) =>
            new SegmentWaypointProposal(aggregate.WaypointIds[sourceIndex], position, null)).ToArray();

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.SegmentId, aggregate.FromPlaceId, aggregate.ToPlaceId, proposal, null));

        Assert.True(result.Succeeded);
        Assert.Equal(order.Select(index => aggregate.WaypointIds[index]),
            await context.Set<SegmentWaypoint>().Where(item => item.SegmentId == aggregate.SegmentId)
                .OrderBy(item => item.Position).Select(item => item.PlaceId).ToListAsync());
    }

    /// <summary>Add, remove, and reorder are committed as one complete PostgreSQL association replacement.</summary>
    [PostgresFact]
    public async Task Reconcile_AddRemoveAndReorder_CommitsOneFinalSet()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var expected = new[] { aggregate.WaypointIds[2], aggregate.AdditionalPlaceId, aggregate.WaypointIds[0] };

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.SegmentId, aggregate.FromPlaceId, aggregate.ToPlaceId,
                expected.Select((id, position) => new SegmentWaypointProposal(id, position, null)).ToArray(), null));

        Assert.True(result.Succeeded);
        Assert.Equal(expected, await context.Set<SegmentWaypoint>().Where(item => item.SegmentId == aggregate.SegmentId)
            .OrderBy(item => item.Position).Select(item => item.PlaceId).ToArrayAsync());
    }

    /// <summary>A provider failure after deletion rolls back storage and selectively reloads usable tracked state.</summary>
    [PostgresFact]
    public async Task Reconcile_ProviderFailure_RollsBackAndRestoresTrackedAggregate()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        var interceptor = new FailNextSaveInterceptor();
        await using var context = _fixture.CreateContext(interceptor);
        var segment = await SegmentRouteReconciler.LoadAggregateAsync(context, aggregate.SegmentId);
        var unrelated = await context.Users.FirstAsync();
        var original = segment!.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId).ToArray();
        var originalFromPlaceId = segment.FromPlaceId;
        interceptor.Arm();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, aggregate.AdditionalPlaceId, segment.ToPlaceId,
                original.Reverse().Select((id, position) => new SegmentWaypointProposal(id, position, null)).ToArray(), null)));

        Assert.Equal(original, segment.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId));
        Assert.Equal(originalFromPlaceId, segment.FromPlaceId);
        Assert.Equal(originalFromPlaceId, segment.FromPlace!.Id);
        Assert.Equal(EntityState.Unchanged, context.Entry(segment).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(unrelated).State);
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        await context.SaveChangesAsync();
        await using var verification = _fixture.CreateContext();
        Assert.Equal(original, await verification.Set<SegmentWaypoint>().Where(item => item.SegmentId == segment.Id)
            .OrderBy(item => item.Position).Select(item => item.PlaceId).ToArrayAsync());
    }

    /// <summary>Rejects a dirty caller context before reconciliation can save or overwrite its pending Trip edit.</summary>
    [PostgresFact]
    public async Task Reconcile_DirtyContextRejectsAndPreservesPendingTripEdit()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var segment = await context.Segments.SingleAsync(item => item.Id == aggregate.SegmentId);
        var trip = await context.Trips.SingleAsync(item => item.Id == segment.TripId);
        trip.Name = "Pending caller edit";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, aggregate.FromPlaceId, aggregate.ToPlaceId, [], null)));

        Assert.Contains("clean", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Pending caller edit", trip.Name);
        Assert.Equal(EntityState.Modified, context.Entry(trip).State);
        await using var verification = _fixture.CreateContext();
        Assert.NotEqual("Pending caller edit", await verification.Trips.Where(item => item.Id == trip.Id).Select(item => item.Name).SingleAsync());
        Assert.Equal(aggregate.WaypointIds, await verification.Set<SegmentWaypoint>()
            .Where(item => item.SegmentId == segment.Id).OrderBy(item => item.Position).Select(item => item.PlaceId).ToArrayAsync());
    }

    /// <summary>Uses a non-cancelled cleanup path after cancellation occurs after destructive waypoint deletion.</summary>
    [PostgresFact]
    public async Task Reconcile_CancellationAfterDeleteRollsBackAndLeavesReusableContext()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelNextSaveInterceptor(cancellation);
        await using var context = _fixture.CreateContext(interceptor);
        var segment = await SegmentRouteReconciler.LoadAggregateAsync(context, aggregate.SegmentId);
        var original = segment!.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId).ToArray();
        interceptor.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, aggregate.FromPlaceId, aggregate.ToPlaceId, [], null), cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(original, segment.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId));
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        await context.SaveChangesAsync(CancellationToken.None);
        await using var verification = _fixture.CreateContext();
        Assert.Equal(original, await verification.Set<SegmentWaypoint>().Where(item => item.SegmentId == segment.Id)
            .OrderBy(item => item.Position).Select(item => item.PlaceId).ToArrayAsync());
    }

    /// <summary>Surfaces both operation and rollback failures and invalidates the unsafe caller context.</summary>
    [PostgresFact]
    public async Task Reconcile_RollbackFailurePreservesBothFailuresAndInvalidatesContext()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        var saveFailure = new FailNextSaveInterceptor();
        var rollbackFailure = new RollbackFailureInterceptor();
        await using var context = _fixture.CreateContext(saveFailure, rollbackFailure);
        saveFailure.Arm();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.SegmentId, aggregate.FromPlaceId, aggregate.ToPlaceId, [], null)));

        Assert.Contains("Forced waypoint persistence failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Forced waypoint rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
    }

    /// <summary>Requires concurrent same-Segment proposals to acquire the provider row lock before aggregate loading.</summary>
    [PostgresFact]
    public async Task Reconcile_ConcurrentSameSegmentProposalsUseCanonicalRowLock()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedOrderedAggregateAsync();
        var firstLock = new SegmentLockCommandInterceptor();
        var secondLock = new SegmentLockCommandInterceptor();
        await using var first = _fixture.CreateContext(firstLock);
        await using var second = _fixture.CreateContext(secondLock);
        var firstProposal = aggregate.WaypointIds.Reverse()
            .Select((id, position) => new SegmentWaypointProposal(id, position, null)).ToArray();
        var secondProposal = new[] { aggregate.AdditionalPlaceId }
            .Select((id, position) => new SegmentWaypointProposal(id, position, null)).ToArray();

        var results = await Task.WhenAll(
            SegmentRouteReconciler.ReconcileAsync(first,
                new(aggregate.SegmentId, aggregate.FromPlaceId, aggregate.ToPlaceId, firstProposal, null)),
            SegmentRouteReconciler.ReconcileAsync(second,
                new(aggregate.SegmentId, aggregate.FromPlaceId, aggregate.ToPlaceId, secondProposal, null)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, firstLock.LockCount);
        Assert.Equal(1, secondLock.LockCount);
        await using var verification = _fixture.CreateContext();
        var stored = await verification.Set<SegmentWaypoint>().Where(item => item.SegmentId == aggregate.SegmentId)
            .OrderBy(item => item.Position).Select(item => item.PlaceId).ToArrayAsync();
        Assert.True(stored.SequenceEqual(firstProposal.Select(item => item.PlaceId))
            || stored.SequenceEqual(secondProposal.Select(item => item.PlaceId)));
    }

    /// <summary>PostgreSQL persistence retains the validated geometry copy after caller mutation.</summary>
    [PostgresFact]
    public async Task Reconcile_DefensiveGeometryCopy_PersistsCanonicalCoordinates()
    {
        _fixture.RequireAvailable();
        var seeded = await SeedPlacesAsync();
        var segment = NewSegment(seeded, 7);
        await using var context = _fixture.CreateContext();
        context.Segments.Add(segment);
        await context.SaveChangesAsync();
        var geometry = Line((1, 1), (2, 2), (3, 3));

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, seeded.PlaceIds[0], seeded.PlaceIds[2], [new(seeded.PlaceIds[1], 0, 1)], geometry));
        geometry.GetCoordinateN(1).X = 99;
        geometry.SRID = 3857;

        Assert.True(result.Succeeded);
        Assert.NotSame(geometry, segment.RouteGeometry);
        await using var verification = _fixture.CreateContext();
        var stored = await verification.Segments.Where(item => item.Id == segment.Id).Select(item => item.RouteGeometry).SingleAsync();
        Assert.Equal(2, stored!.GetCoordinateN(1).X);
        Assert.Equal(4326, stored.SRID);
    }

    /// <summary>Canonical provider loading distinguishes missing and cross-trip proposal identities.</summary>
    [PostgresFact]
    public async Task Reconcile_CanonicalIdentityFailures_AreDeterministicAndAtomic()
    {
        _fixture.RequireAvailable();
        var aggregate = await SeedAggregateAsync();
        await using var context = _fixture.CreateContext();
        var missing = await SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.SegmentId, Guid.NewGuid(), aggregate.SecondPlaceId, [new(Guid.NewGuid(), 0, null)], null));
        var crossTrip = await SegmentRouteReconciler.ReconcileAsync(context,
            new(aggregate.SegmentId, aggregate.FirstPlaceId, aggregate.SecondPlaceId,
                [new(aggregate.ForeignPlaceId, 0, null)], null));

        Assert.Contains("From place was not found.", missing.Errors);
        Assert.Contains("Waypoint place at position 0 was not found.", missing.Errors);
        Assert.Contains("Every waypoint place must belong to the segment trip.", crossTrip.Errors);
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    private async Task<(Guid SegmentId, Guid FirstPlaceId, Guid SecondPlaceId, Guid ForeignPlaceId)> SeedAggregateAsync()
    {
        var seeded = await SeedPlacesAsync(includeForeignTrip: true);
        await using var context = _fixture.CreateContext();
        var places = await context.Places.Include(place => place.Region).Where(place => seeded.PlaceIds.Contains(place.Id)).ToDictionaryAsync(place => place.Id);
        var segment = NewSegment(seeded, 1);
        context.Segments.Add(segment);
        await context.SaveChangesAsync();
        Assert.True((await SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, seeded.PlaceIds[0], seeded.PlaceIds[2], [new(seeded.PlaceIds[1], 0, 2)],
                Line((1, 1), (1.5, 1.5), (2, 2), (3, 3))))).Succeeded);
        return (segment.Id, seeded.PlaceIds[1], seeded.PlaceIds[3], seeded.ForeignPlaceId!.Value);
    }

    private async Task<(Guid SegmentId, Guid FromPlaceId, Guid ToPlaceId, Guid[] WaypointIds, Guid AdditionalPlaceId)> SeedOrderedAggregateAsync()
    {
        var seeded = await SeedPlacesAsync();
        var segment = NewSegment(seeded, 8);
        await using var context = _fixture.CreateContext();
        context.Segments.Add(segment);
        await context.SaveChangesAsync();
        var waypointIds = seeded.PlaceIds[1..5];
        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(segment.Id, seeded.PlaceIds[0], seeded.PlaceIds[5],
                waypointIds.Select((id, position) => new SegmentWaypointProposal(id, position, null)).ToArray(), null));
        Assert.True(result.Succeeded);
        return (segment.Id, seeded.PlaceIds[0], seeded.PlaceIds[5], waypointIds, seeded.PlaceIds[6]);
    }

    private async Task<(Guid TripId, string UserId, Guid[] PlaceIds, Guid? ForeignPlaceId)> SeedPlacesAsync(bool includeForeignTrip = false)
    {
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Waypoint provider fixture" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Route" };
        var places = Enumerable.Range(1, 7).Select(index => new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = $"Place {index}", Location = new Point(index, index) { SRID = 4326 }
        }).ToArray();
        trip.Regions.Add(region);
        foreach (var place in places) region.Places.Add(place);
        await using var context = _fixture.CreateContext();
        context.Trips.Add(trip);
        Guid? foreignPlaceId = null;
        if (includeForeignTrip)
        {
            var foreignTrip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Foreign trip" };
            var foreignRegion = new Region { Id = Guid.NewGuid(), Trip = foreignTrip, TripId = foreignTrip.Id, UserId = user.Id, Name = "Foreign" };
            var foreign = new Place { Id = Guid.NewGuid(), Region = foreignRegion, RegionId = foreignRegion.Id, UserId = user.Id, Name = "Foreign", Location = new Point(9, 9) { SRID = 4326 } };
            foreignTrip.Regions.Add(foreignRegion);
            foreignRegion.Places.Add(foreign);
            context.Trips.Add(foreignTrip);
            foreignPlaceId = foreign.Id;
        }
        await context.SaveChangesAsync();
        return (trip.Id, user.Id, places.Select(place => place.Id).ToArray(), foreignPlaceId);
    }

    private static Segment NewSegment((Guid TripId, string UserId, Guid[] PlaceIds, Guid? ForeignPlaceId) seeded, int order) =>
        new() { Id = Guid.NewGuid(), TripId = seeded.TripId, UserId = seeded.UserId, Mode = "walk", DisplayOrder = order };

    private static LineString Line(params (double X, double Y)[] points) =>
        new(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()) { SRID = 4326 };

    private static async Task AssertInsertFailsAsync(ApplicationDbContext context, Guid segmentId, Guid placeId, int position, int? routeVertexIndex)
    {
        await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."SegmentWaypoints" ("SegmentId", "PlaceId", "Position", "RouteVertexIndex") VALUES ({segmentId}, {placeId}, {position}, {routeVertexIndex})"""));
    }

    private sealed class FailNextSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        /// <summary>Arms one deterministic persistence failure after provider-side waypoint deletion.</summary>
        internal void Arm() => _armed = true;

        /// <summary>Throws once at the SaveChanges seam used to insert the final association set.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed) return base.SavingChangesAsync(eventData, result, cancellationToken);
            _armed = false;
            throw new InvalidOperationException("Forced waypoint persistence failure.");
        }
    }

    private sealed class CancelNextSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool _armed;

        /// <summary>Arms cancellation at the SaveChanges seam after the reconciler's provider-side deletion.</summary>
        internal void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed) return base.SavingChangesAsync(eventData, result, cancellationToken);
            _armed = false;
            cancellation.Cancel();
            throw new OperationCanceledException("Forced cancellation after waypoint deletion.", cancellation.Token);
        }
    }

    private sealed class RollbackFailureInterceptor : DbTransactionInterceptor
    {
        /// <summary>Fails mandatory asynchronous rollback to exercise context invalidation.</summary>
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Forced waypoint rollback failure.");
        }
    }


    private sealed class SegmentLockCommandInterceptor : DbCommandInterceptor
    {
        /// <summary>Gets the number of canonical Segment row-lock commands executed by one reconciliation.</summary>
        internal int LockCount { get; private set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("Segments", StringComparison.Ordinal)) LockCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("Segments", StringComparison.Ordinal)) LockCount++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
