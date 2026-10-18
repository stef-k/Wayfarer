using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Models;

/// <summary>Executes lifecycle cancellation, provider, rollback, recovery, and invalidation paths.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleRecoveryPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Preserves a partially tracked sibling through three post-SQL failures and a later ordinary save.</summary>
    [PostgresFact]
    public async Task PlaceUpdate_ExhaustedSerializationRetries_PreservesPendingPartialTrackerSibling()
    {
        var seeded = await SeedAsync(includeSibling: true);
        var failure = new PostSqlSerializationInterceptor();
        await using var context = fixture.CreateContext(failure);
        var sibling = await context.Places.SingleAsync(item =>
            item.Name == "Sibling" && item.Region.TripId == seeded.TripId);
        Assert.Single(context.ChangeTracker.Entries());
        Assert.Null(sibling.Region);
        sibling.Notes = "pending sibling edit";
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => Service(context, confirmation).UpdatePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId,
            new(sibling.RegionId, "Rejected", "rejected notes", "rejected address", "marker", "bg-red",
                new Point(2, 2) { SRID = 4326 }, 1), CancellationToken.None));

        Assert.Equal(3, failure.Failures.Count);
        Assert.All(failure.Failures, exception => Assert.Equal(PostgresErrorCodes.SerializationFailure, exception.SqlState));
        Assert.Same(failure.Failures[^1], thrown);
        var recoveredState = context.Entry(sibling).State;
        var recoveredNotes = sibling.Notes;
        var recoveredSibling = context.ChangeTracker.Entries<Place>()
            .SingleOrDefault(entry => entry.Entity.Id == sibling.Id)?.Entity;
        output.WriteLine($"Recovered sibling: state={recoveredState}; same instance={ReferenceEquals(sibling, recoveredSibling)}; notes={recoveredNotes}");
        await AssertStoredAsync(seeded);
        await context.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        var storedSibling = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == sibling.Id);
        Assert.Equal("pending sibling edit", storedSibling.Notes);
        Assert.Equal(EntityState.Modified, recoveredState);
        Assert.Equal("pending sibling edit", recoveredNotes);
        Assert.Same(sibling, recoveredSibling);
        Assert.Equal(4, storedSibling.DisplayOrder);
        var target = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == seeded.WaypointId);
        Assert.Equal("Waypoint", target.Name);
        Assert.Null(target.Notes);
        Assert.Null(target.Address);
        Assert.Equal(sibling.RegionId, target.RegionId);
        Assert.Equal(2, target.DisplayOrder);
        Assert.Equal(new Coordinate(2, 2), target.Location!.Coordinate);
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(segment.EstimatedDistanceKm);
        Assert.Null(segment.EstimatedDuration);
        Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry!.Coordinates[1]);
        var waypoint = await verification.Set<SegmentWaypoint>().AsNoTracking()
            .SingleAsync(item => item.SegmentId == seeded.SegmentId);
        Assert.Equal(0, waypoint.Position);
        Assert.Equal(1, waypoint.RouteVertexIndex);
        AssertClean(context);
    }

    /// <summary>Retries with the original sibling instance and commits its pending edit before a later ordinary save.</summary>
    [PostgresTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PlaceUpdate_SerializationRetry_PreservesPendingPartialTrackerSibling(int failedAttempts)
    {
        var seeded = await SeedAsync(includeSibling: true);
        var failure = new PostSqlSerializationInterceptor(failedAttempts);
        await using var context = fixture.CreateContext(failure);
        var sibling = await context.Places.SingleAsync(item =>
            item.Name == "Sibling" && item.Region.TripId == seeded.TripId);
        Assert.Single(context.ChangeTracker.Entries());
        sibling.Notes = "pending sibling edit";
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());

        var result = await Service(context, confirmation).UpdatePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId,
            new(sibling.RegionId, "Accepted", "accepted notes", "accepted address", "marker", "bg-red",
                new Point(2, 2) { SRID = 4326 }), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(failedAttempts, failure.Failures.Count);
        Assert.Same(sibling, context.Places.Local.Single(item => item.Id == sibling.Id));
        Assert.Equal(EntityState.Unchanged, context.Entry(sibling).State);
        await using var verification = fixture.CreateContext();
        Assert.Equal("pending sibling edit", await verification.Places.AsNoTracking()
            .Where(item => item.Id == sibling.Id).Select(item => item.Notes).SingleAsync());
        var target = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == seeded.WaypointId);
        Assert.Equal("Accepted", target.Name);
        Assert.Equal(2, target.DisplayOrder);
        sibling.Notes = "subsequent sibling edit";
        await context.SaveChangesAsync();
        Assert.Equal("subsequent sibling edit", await verification.Places.AsNoTracking()
            .Where(item => item.Id == sibling.Id).Select(item => item.Notes).SingleAsync());
        AssertClean(context);
    }

    /// <summary>Uses non-cancelled rollback/recovery after cancellation following destructive waypoint work.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_CancellationAfterDestructiveWork_RestoresStateAndReusableContext()
    {
        var seeded = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var failure = new SaveFailureInterceptor(() =>
        {
            cancellation.Cancel();
            return new OperationCanceledException("Lifecycle cancellation.", cancellation.Token);
        });
        await using var context = fixture.CreateContext(failure);
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var token = await ChallengeAsync(seeded, confirmation);
        failure.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        AssertClean(context);
        await context.SaveChangesAsync(CancellationToken.None);
        await AssertStoredAsync(seeded);
    }

    /// <summary>Retains the exact PostgreSQL serialization exception after successful mandatory cleanup.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_SerializationFailure_RetainsOriginalAndRestoresState()
    {
        var seeded = await SeedAsync();
        var postgres = new PostgresException(
            "serialization", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure,
            null!, null!, 0, 0, null!, null!, "public", null!, null!, null!,
            null!, "predicate.c", "1", "serialization_failure");
        var original = new DbUpdateException("Lifecycle serialization failure.", postgres);
        var failure = new SaveFailureInterceptor(() => original);
        await using var context = fixture.CreateContext(failure);
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var token = await ChallengeAsync(seeded, confirmation);
        failure.Arm();

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None));

        Assert.Same(original, thrown);
        AssertClean(context);
        await AssertStoredAsync(seeded);
    }

    /// <summary>Aggregates operation and rollback failures and disposes the incoherent context.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_RollbackFailure_AggregatesCleanupAndInvalidatesContext()
    {
        var seeded = await SeedAsync();
        var failure = new SaveFailureInterceptor(() => new InvalidOperationException("Original lifecycle failure."));
        await using var context = fixture.CreateContext(failure, new RollbackFailureInterceptor());
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var token = await ChallengeAsync(seeded, confirmation);
        failure.Arm();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None));

        Assert.Contains("Original lifecycle failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Lifecycle rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Places.CountAsync());
        await AssertStoredAsync(seeded);
    }

    /// <summary>Aggregates operation and recovery-query failures and disposes the incoherent context.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_RecoveryFailure_AggregatesCleanupAndInvalidatesContext()
    {
        var seeded = await SeedAsync();
        var failure = new SaveFailureInterceptor(() => new InvalidOperationException("Original lifecycle failure."));
        await using var context = fixture.CreateContext(failure, new RecoveryFailureInterceptor());
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var token = await ChallengeAsync(seeded, confirmation);
        failure.Arm();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None));

        Assert.Contains("Original lifecycle failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Lifecycle recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertStoredAsync(seeded);
    }

    /// <summary>Retains the original plus both cleanup failures when rollback and recovery independently fail.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_RollbackAndRecoveryFailure_AggregatesAllAndInvalidatesContext()
    {
        var seeded = await SeedAsync();
        var failure = new SaveFailureInterceptor(() => new InvalidOperationException("Original lifecycle failure."));
        await using var context = fixture.CreateContext(
            failure, new RollbackFailureInterceptor(), new RecoveryFailureInterceptor());
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var token = await ChallengeAsync(seeded, confirmation);
        failure.Arm();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None));

        Assert.Contains("Original lifecycle failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Lifecycle rollback failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Lifecycle recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Regions.CountAsync());
        await AssertStoredAsync(seeded);
    }

    /// <summary>Seeds a waypoint route, optionally with a sibling outside the Segment anchor set.</summary>
    private async Task<RecoverySeed> SeedAsync(bool includeSibling = false)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Recovery", UpdatedAt = DateTime.UtcNow };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Region", DisplayOrder = 1
        };
        trip.Regions.Add(region);
        var from = Place(region, user.Id, "From", 1, 1);
        var waypoint = Place(region, user.Id, "Waypoint", 2, 2);
        var to = Place(region, user.Id, "To", 3, 3);
        if (includeSibling) Place(region, user.Id, "Sibling", 4, 4);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 }
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
            Position = 0, RouteVertexIndex = 1
        });
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        var storedUpdatedAt = await context.Trips.AsNoTracking().Where(item => item.Id == trip.Id)
            .Select(item => item.UpdatedAt).SingleAsync();
        return new(user.Id, trip.Id, waypoint.Id, segment.Id, storedUpdatedAt);
    }

    private async Task<string> ChallengeAsync(
        RecoverySeed seeded,
        LifecycleDependencyConfirmation confirmation)
    {
        await using var context = fixture.CreateContext();
        var result = await Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, null, CancellationToken.None);
        return result.Warning!.ConfirmationToken;
    }

    private async Task AssertStoredAsync(RecoverySeed seeded)
    {
        await using var verification = fixture.CreateContext();
        Assert.True(await verification.Places.AnyAsync(item => item.Id == seeded.WaypointId));
        var segment = await verification.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(seeded.UpdatedAt, await verification.Trips.Where(item => item.Id == seeded.TripId)
            .Select(item => item.UpdatedAt).SingleAsync());
        Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry!.Coordinates[1]);
        var waypoint = await verification.Set<SegmentWaypoint>().AsNoTracking()
            .SingleAsync(item => item.SegmentId == seeded.SegmentId);
        Assert.Equal(0, waypoint.Position);
        Assert.Equal(1, waypoint.RouteVertexIndex);
    }

    private static void AssertClean(ApplicationDbContext context) =>
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private static PlaceRegionLifecycleService Service(
        ApplicationDbContext context,
        LifecycleDependencyConfirmation confirmation) => new(context, confirmation);

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1,
            Location = new Point(x, y) { SRID = 4326 }
        };
        region.Places.Add(place);
        return place;
    }

    private sealed record RecoverySeed(
        string UserId,
        Guid TripId,
        Guid WaypointId,
        Guid SegmentId,
        DateTime UpdatedAt);

    /// <summary>Raises real PostgreSQL serialization errors after SQL and EF acceptance on the requested early attempts.</summary>
    private sealed class PostSqlSerializationInterceptor(int failedAttempts = 3) : SaveChangesInterceptor
    {
        /// <summary>Records the original server exceptions in attempt order.</summary>
        internal List<PostgresException> Failures { get; } = [];

        /// <summary>Aborts the active transaction only after the lifecycle save has completed.</summary>
        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Failures.Count >= failedAttempts) return result;
            try
            {
                await eventData.Context!.Database.ExecuteSqlRawAsync(
                    "DO $$ BEGIN RAISE EXCEPTION 'Injected post-SQL serialization failure' USING ERRCODE = '40001'; END $$;",
                    cancellationToken);
            }
            catch (PostgresException exception)
            {
                Failures.Add(exception);
                throw;
            }
            return result;
        }
    }

    private sealed class SaveFailureInterceptor(Func<Exception> failure) : SaveChangesInterceptor
    {
        private bool _armed;

        /// <summary>Arms one deterministic failure at the lifecycle SaveChanges boundary.</summary>
        internal void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed) return base.SavingChangesAsync(eventData, result, cancellationToken);
            _armed = false;
            throw failure();
        }
    }

    private sealed class RollbackFailureInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Lifecycle rollback failure.");
    }

    private sealed class RecoveryFailureInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("LIFECYCLE RECOVERY", StringComparison.Ordinal))
                throw new InvalidOperationException("Lifecycle recovery failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
