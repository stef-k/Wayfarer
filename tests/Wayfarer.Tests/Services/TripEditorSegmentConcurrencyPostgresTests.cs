using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
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
}
