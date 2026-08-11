using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes deterministic token, confirmation-drift, and profile-lock #407 PostgreSQL scenarios.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripEditorSegmentConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A token read before a gated concurrent Segment update is rejected after the row lock wait.</summary>
    [PostgresFact]
    public async Task GatedConcurrentSegmentUpdate_RejectsStaleAggregateToken()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        await using var serviceContext = fixture.CreateContext();
        await serviceContext.Database.OpenConnectionAsync();
        var token = await support.TokenAsync(serviceContext, seed);
        var servicePid = await BackendPidAsync(serviceContext);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        const string concurrentNotes = "gated concurrent notes";
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.\"Segments\" SET \"Notes\" = {concurrentNotes} WHERE \"Id\" = {seed.SegmentId!.Value}");

        var mutation = support.Service(serviceContext).UpdateSegmentAsync(seed.TripId, seed.SegmentId.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.FirstWaypointId, seed.SecondWaypointId], [null, null], token),
            null, CancellationToken.None);
        await WaitUntilBlockedAsync(servicePid);
        await transaction.CommitAsync();

        var outcome = await mutation;
        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        Assert.Equal("segment-aggregate-stale", Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict).Code);
        await using var verification = fixture.CreateContext();
        Assert.Equal("gated concurrent notes", await verification.Segments.Where(item => item.Id == seed.SegmentId)
            .Select(item => item.Notes).SingleAsync());
    }

    /// <summary>Anchor, geometry, and profile drift each invalidate an already-issued route-clear confirmation.</summary>
    [PostgresTheory]
    [InlineData("anchor")]
    [InlineData("geometry")]
    [InlineData("profile")]
    public async Task CanonicalDrift_RejectsStaleRouteClearConfirmation(string drift)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        await using var context = fixture.CreateContext();
        var service = support.Service(context);
        var token = await support.TokenAsync(context, seed);
        var challenge = await service.UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId, seed.SecondWaypointId], [null, null], token),
            null, CancellationToken.None);
        var confirmation = Assert.IsType<EditorSegmentConflictDto>(challenge.Conflict).ConfirmationToken;
        Assert.False(string.IsNullOrWhiteSpace(confirmation));

        await using (var driftContext = fixture.CreateContext())
        {
            if (drift == "anchor")
                await driftContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE public.\"Segments\" SET \"FromPlaceId\" = {seed.AlternateId} WHERE \"Id\" = {seed.SegmentId.Value}");
            else if (drift == "geometry")
            {
                var changed = new LineString([new Coordinate(0, 0), new Coordinate(1, 1.25), new Coordinate(2, 2), new Coordinate(3, 3)]) { SRID = 4326 };
                await driftContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE public.\"Segments\" SET \"RouteGeometry\" = {changed} WHERE \"Id\" = {seed.SegmentId.Value}");
            }
            else
                await driftContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE public.\"Segments\" SET \"Mode\" = {seed.SecondProfileKey}, \"TransportProfileId\" = {seed.SecondProfileId} WHERE \"Id\" = {seed.SegmentId.Value}");
        }
        context.ChangeTracker.Clear();
        var refreshedToken = await support.TokenAsync(context, seed);
        var outcome = await service.UpdateSegmentAsync(seed.TripId, seed.SegmentId.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId, seed.SecondWaypointId], [null, null], refreshedToken),
            confirmation, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        var conflict = Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict);
        Assert.Equal("segment-route-clear-confirmation-stale", conflict.Code);
        Assert.False(string.IsNullOrWhiteSpace(conflict.ConfirmationToken));
        Assert.NotEqual(confirmation, conflict.ConfirmationToken);
    }

    /// <summary>Current and proposed profile rows are locked exactly once in ascending GUID order.</summary>
    [PostgresFact]
    public async Task CurrentAndProposedProfiles_LockInAscendingGuidOrder()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var recorder = new ProfileLockRecorder();
        await using var context = fixture.CreateContext(recorder);
        var token = await support.TokenAsync(context, seed);
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.FirstWaypointId, seed.SecondWaypointId], [null, null], token,
                mode: seed.SecondProfileKey), null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        Assert.Equal(new[] { seed.FirstProfileId, seed.SecondProfileId }.Order(), recorder.ProfileIds);
    }

    /// <summary>A PostgreSQL serialization rejection after token comparison is an exact write conflict with no partial aggregate.</summary>
    [PostgresFact]
    public async Task PostComparisonSerializationFailure_ReturnsWriteConflictWithoutPartialState()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        await using var context = fixture.CreateContext(new SerializationSaveInterceptor());
        var token = await support.TokenAsync(context, seed);
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey, notes: "must not persist"), null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        Assert.Equal("segment-write-conflict", Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict).Code);
        await using var verification = fixture.CreateContext();
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(verification, seed.SegmentId.Value);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal(seed.FirstProfileId, stored.TransportProfileId);
        Assert.Equal("original notes", stored.Notes);
    }

    /// <summary>A property-only notes write queued behind an editor replacement preserves both committed intentions.</summary>
    [PostgresFact]
    public async Task NotesOnlyUpdateAndAggregateReplacement_PreserveBothChanges()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var gate = new SaveGateInterceptor();
        await using var editor = fixture.CreateContext(gate);
        var token = await support.TokenAsync(editor, seed);
        var originalVersion = (await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(editor, seed.SegmentId!.Value)).RowVersion;
        var aggregate = support.Service(editor).UpdateSegmentAsync(seed.TripId, seed.SegmentId.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey, notes: "editor notes"), null, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var notes = fixture.CreateContext();
        await notes.Database.OpenConnectionAsync();
        var notesPid = await BackendPidAsync(notes);
        var notesWrite = notes.Segments.Where(item => item.Id == seed.SegmentId && item.UserId == seed.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Notes, "mobile notes"));
        await WaitUntilBlockedAsync(notesPid);
        gate.ReleaseSave.TrySetResult();

        var outcome = await aggregate;
        Assert.Equal(1, await notesWrite);
        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        await using var verification = fixture.CreateContext();
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(verification, seed.SegmentId.Value);
        Assert.Equal([seed.AlternateId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal(seed.SecondProfileId, stored.TransportProfileId);
        Assert.Equal("mobile notes", stored.Notes);
        Assert.NotEqual(originalVersion, stored.RowVersion);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Result!.Data.AggregateConcurrencyToken));
    }

    /// <summary>A referenced profile-speed reconciliation waits behind the editor and preserves the committed replacement.</summary>
    [PostgresFact]
    public async Task AggregateReplacementAndReferencedProfileSpeedReconciliation_Serialize()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var gate = new SaveGateInterceptor();
        await using var editor = fixture.CreateContext(gate);
        var token = await support.TokenAsync(editor, seed);
        var aggregate = support.Service(editor).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey), null, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var profile = fixture.CreateContext();
        await profile.Database.OpenConnectionAsync();
        var profilePid = await BackendPidAsync(profile);
        var reconciliation = TransportProfileMeasurementReconciler.ReconcileAsync(
            profile, seed.SecondProfileId, 25, seed.UserId, CancellationToken.None);
        await WaitUntilBlockedAsync(profilePid);
        gate.ReleaseSave.TrySetResult();

        Assert.Equal(EditorRegionMutationStatus.Success, (await aggregate).Status);
        var profileFailure = await Assert.ThrowsAnyAsync<Exception>(() => reconciliation);
        Assert.Contains(PostgresErrorCodes.SerializationFailure, profileFailure.ToString(), StringComparison.Ordinal);
        await AssertReplacementAsync(seed, "updated notes");
        await using var verification = fixture.CreateContext();
        Assert.Equal(20, await verification.Set<TransportProfile>().Where(item => item.Id == seed.SecondProfileId)
            .Select(item => item.PlanningSpeedKmh).SingleAsync());
    }

    /// <summary>A Place lifecycle mutation retries after the editor adds its dependency and preserves both changes.</summary>
    [PostgresFact]
    public async Task AggregateReplacementAndPlaceLifecycleMutation_Serialize()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var gate = new SaveGateInterceptor();
        await using var editor = fixture.CreateContext(gate);
        var token = await support.TokenAsync(editor, seed);
        var aggregate = support.Service(editor).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey),
            null, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var lifecycleContext = fixture.CreateContext();
        await lifecycleContext.Database.OpenConnectionAsync();
        var lifecyclePid = await BackendPidAsync(lifecycleContext);
        var placeState = await lifecycleContext.Places.AsNoTracking()
            .Where(item => item.Id == seed.AlternateId)
            .Select(item => new { item.RegionId, item.Location }).SingleAsync();
        var lifecycle = new PlaceRegionLifecycleService(lifecycleContext,
            new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));
        var placeUpdate = lifecycle.UpdatePlaceAsync(seed.TripId, seed.AlternateId, seed.UserId,
            new PlaceLifecycleUpdate(placeState.RegionId, "Lifecycle renamed", "", "", "", "", placeState.Location),
            CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);
        gate.ReleaseSave.TrySetResult();

        Assert.Equal(EditorRegionMutationStatus.Success, (await aggregate).Status);
        Assert.True((await placeUpdate).Succeeded);
        await AssertReplacementAsync(seed, "updated notes");
        await using var verification = fixture.CreateContext();
        Assert.Equal("Lifecycle renamed", await verification.Places.Where(item => item.Id == seed.AlternateId)
            .Select(item => item.Name).SingleAsync());
    }

    /// <summary>Region deletion refreshes after the editor creates a new dependency and returns a stale lifecycle warning.</summary>
    [PostgresFact]
    public async Task AggregateReplacementAndRegionLifecycleMutation_Serialize()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var auxiliaryRegionId = Guid.NewGuid();
        await using (var setup = fixture.CreateContext())
        {
            setup.Regions.Add(new Region { Id = auxiliaryRegionId, TripId = seed.TripId, UserId = seed.UserId, Name = "Disposable region", DisplayOrder = 2 });
            await setup.SaveChangesAsync();
            await setup.Places.Where(item => item.Id == seed.AlternateId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.RegionId, auxiliaryRegionId));
        }

        await using var lifecycleContext = fixture.CreateContext();
        await lifecycleContext.Database.OpenConnectionAsync();
        var lifecycle = new PlaceRegionLifecycleService(lifecycleContext,
            new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));
        var challenge = await lifecycle.DeleteRegionAsync(
            seed.TripId, auxiliaryRegionId, seed.UserId, null, CancellationToken.None);
        var confirmation = challenge.Warning!.ConfirmationToken;

        var gate = new SaveGateInterceptor();
        await using var editor = fixture.CreateContext(gate);
        var token = await support.TokenAsync(editor, seed);
        var aggregate = support.Service(editor).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey),
            null, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lifecyclePid = await BackendPidAsync(lifecycleContext);
        var deletion = lifecycle.DeleteRegionAsync(
            seed.TripId, auxiliaryRegionId, seed.UserId, confirmation, CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);
        gate.ReleaseSave.TrySetResult();

        Assert.Equal(EditorRegionMutationStatus.Success, (await aggregate).Status);
        var deletionResult = await deletion;
        Assert.False(deletionResult.Succeeded);
        Assert.Equal("lifecycle-confirmation-stale", deletionResult.Warning!.Code);
        await AssertReplacementAsync(seed, "updated notes");
    }

    private async Task AssertReplacementAsync(SegmentSeed seed, string notes)
    {
        await using var verification = fixture.CreateContext();
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(verification, seed.SegmentId!.Value);
        Assert.Equal([seed.AlternateId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal(seed.SecondProfileId, stored.TransportProfileId);
        Assert.Equal(notes, stored.Notes);
    }

    private async Task WaitUntilBlockedAsync(int backendPid)
    {
        await using var context = fixture.CreateContext();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var blockers = await context.Database.SqlQueryRaw<int>(
                "SELECT cardinality(pg_blocking_pids({0})) AS \"Value\"", backendPid).SingleAsync();
            if (blockers > 0) return;
            await Task.Yield();
        }
        throw new TimeoutException("The #407 editor mutation did not enter the expected PostgreSQL row-lock wait.");
    }

    private static Task<int> BackendPidAsync(ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private sealed class ProfileLockRecorder : DbCommandInterceptor
    {
        internal List<Guid> ProfileIds { get; } = [];

        /// <summary>Records only the explicit profile FOR UPDATE statements used by the editor mutation.</summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM public.\"TransportProfiles\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                ProfileIds.Add((Guid)command.Parameters[0].Value!);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveGateInterceptor : SaveChangesInterceptor
    {
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the editor transaction after destructive replacement and before its single final save.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class SerializationSaveInterceptor : SaveChangesInterceptor
    {
        /// <summary>Injects the provider's exact serialization code only at the final aggregate save.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new PostgresException(
                "serialization", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure,
                null!, null!, 0, 0, null!, null!, "public", null!, null!, null!,
                null!, "predicate.c", "1", "serialization_failure"));
    }
}
