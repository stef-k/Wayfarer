using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes the property-only notes and owning-Trip timestamp contract against PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentNotesMutationPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A notes write advances the Trip timestamp without changing any aggregate-owned fields.</summary>
    [PostgresFact]
    public async Task Success_AdvancesTripTimestampAndPreservesAggregate()
    {
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext();
        var before = await SnapshotAsync(context, seed);

        Assert.True(await SegmentNotesMutation.UpdateRelationalAsync(
            context, seed.TripId, seed.SegmentId!.Value, seed.UserId, "mobile notes", CancellationToken.None));

        await using var verification = fixture.CreateContext();
        var after = await SnapshotAsync(verification, seed);
        Assert.Equal("mobile notes", after.Notes);
        Assert.True(after.UpdatedAt > before.UpdatedAt);
        Assert.Equal(before with { Notes = "mobile notes", UpdatedAt = after.UpdatedAt }, after);
    }

    /// <summary>A provider failure after the Trip update rolls both properties back.</summary>
    [PostgresFact]
    public async Task ProviderFailure_RollsBackNotesAndTimestamp()
    {
        var seed = await SeedAsync();
        await using var baseline = fixture.CreateContext();
        var before = await SnapshotAsync(baseline, seed);
        await using var context = fixture.CreateContext(new TripUpdateFailureInterceptor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => SegmentNotesMutation.UpdateRelationalAsync(
            context, seed.TripId, seed.SegmentId!.Value, seed.UserId, "mobile notes", CancellationToken.None));

        await using var verification = fixture.CreateContext();
        Assert.Equal(before, await SnapshotAsync(verification, seed));
    }

    /// <summary>Request cancellation after the Trip update rolls both properties back with mandatory cleanup.</summary>
    [PostgresFact]
    public async Task Cancellation_RollsBackNotesAndTimestamp()
    {
        var seed = await SeedAsync();
        await using var baseline = fixture.CreateContext();
        var before = await SnapshotAsync(baseline, seed);
        using var cancellation = new CancellationTokenSource();
        await using var context = fixture.CreateContext(new CancelAfterSegmentUpdateInterceptor(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SegmentNotesMutation.UpdateRelationalAsync(
            context, seed.TripId, seed.SegmentId!.Value, seed.UserId, "mobile notes", cancellation.Token));

        await using var verification = fixture.CreateContext();
        Assert.Equal(before, await SnapshotAsync(verification, seed));
    }

    /// <summary>A non-owner changes neither notes nor the Trip timestamp.</summary>
    [PostgresFact]
    public async Task NonOwner_ChangesNothing()
    {
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext();
        var before = await SnapshotAsync(context, seed);

        Assert.False(await SegmentNotesMutation.UpdateRelationalAsync(
            context, seed.TripId, seed.SegmentId!.Value, "not-owner", "mobile notes", CancellationToken.None));

        await using var verification = fixture.CreateContext();
        Assert.Equal(before, await SnapshotAsync(verification, seed));
    }

    private async Task<SegmentSeed> SeedAsync() =>
        await new TripEditorSegmentMutationPostgresTestSupport(fixture).SeedAsync();

    private static async Task<NotesSnapshot> SnapshotAsync(ApplicationDbContext context, SegmentSeed seed)
    {
        var segment = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, seed.SegmentId!.Value);
        var updatedAt = await context.Trips.AsNoTracking().Where(item => item.Id == seed.TripId)
            .Select(item => item.UpdatedAt).SingleAsync();
        return new(segment.Notes ?? string.Empty, updatedAt, segment.FromPlaceId, segment.ToPlaceId,
            string.Join(',', segment.Waypoints.Select(item => item.PlaceId)),
            string.Join(',', segment.Waypoints.Select(item => item.RouteVertexIndex)),
            segment.TransportProfileId, segment.EstimatedDistanceKm, segment.EstimatedDuration,
            segment.EstimatedDurationSource, segment.RouteGeometry!.AsText());
    }

    private sealed class TripUpdateFailureInterceptor : DbCommandInterceptor
    {
        /// <summary>Fails after the Segment notes statement but before the Trip timestamp statement commits.</summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("UPDATE \"Trips\"", StringComparison.Ordinal)
                ? ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Deterministic notes provider failure."))
                : ValueTask.FromResult(result);
    }

    private sealed class CancelAfterSegmentUpdateInterceptor(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        /// <summary>Cancels the request only after PostgreSQL has executed the notes update.</summary>
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (IsSegmentUpdate(command)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private static bool IsSegmentUpdate(DbCommand command) =>
        command.CommandText.Contains("UPDATE \"Segments\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"Notes\"", StringComparison.Ordinal);

    private sealed record NotesSnapshot(
        string Notes,
        DateTime UpdatedAt,
        Guid? FromPlaceId,
        Guid? ToPlaceId,
        string WaypointIds,
        string WaypointIndices,
        Guid? ProfileId,
        double? Distance,
        TimeSpan? Duration,
        EstimatedDurationSource DurationSource,
        string Route);
}
