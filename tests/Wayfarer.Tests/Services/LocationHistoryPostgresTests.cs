using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves final-row atomicity and durable independent preparation on the guarded database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationHistoryPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task StalePartialEditsDeriveFromFinalMergedRowAndRollbackTogether()
    {
        var owner = await fixture.CreateUserAsync();
        var row = Row(owner.Id);
        await using var first = fixture.CreateContext();
        first.Add(row); await first.SaveChangesAsync();
        await using var stale = fixture.CreateContext();
        var edited = await stale.Locations.SingleAsync(item => item.Id == row.Id);
        row.Address = "Café street"; row.LocalTimestamp = row.LocalTimestamp.AddDays(1);
        await first.SaveChangesAsync();
        edited.Notes = "<p>new <b>words</b></p>";
        await stale.SaveChangesAsync();
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        Assert.Equal("NEW WORDS\nCAFE STREET", saved.HistorySearchText);
        Assert.Equal(row.LocalTimestamp, saved.LocalTimestamp);
        Assert.Equal(row.CaptureLocalTimestamp, saved.CaptureLocalTimestamp);
        row.Address = null;
        await first.SaveChangesAsync();
        saved = await verify.Locations.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        Assert.Equal("NEW WORDS", saved.HistorySearchText);
        Assert.Equal("<p>new <b>words</b></p>", saved.Notes);
        await using (var transaction = await stale.Database.BeginTransactionAsync())
        {
            edited.Notes = "rollback";
            await stale.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        Assert.Equal(saved.HistorySearchText, await verify.Locations.Where(item => item.Id == row.Id).Select(item => item.HistorySearchText).SingleAsync());
    }

    [PostgresFact]
    public async Task PreparationResumesAcrossContextsAndSeparatesPendingUnresolvedAndGenerations()
    {
        var first = await fixture.CreateUserAsync(); var second = await fixture.CreateUserAsync();
        var a = Row(first.Id); var b = Row(second.Id);
        b.Coordinates = new Point(double.NaN, double.NaN) { SRID = 4326 };
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(a, b); await seed.SaveChangesAsync();
            await ClearDerivedAsync(seed, [a.Id, b.Id]);
        }
        await using (var prepare = fixture.CreateContext())
        {
            var service = new LocationHistoryPreparation(prepare);
            await service.BeginAsync("time", true, default);
            await service.BeginAsync("text", true, default);
            Assert.False(await service.PrepareBatchAsync("time", default, 1));
        }
        await using (var resume = fixture.CreateContext())
        {
            var service = new LocationHistoryPreparation(resume);
            var state = await resume.HistoryPreparationStates.AsNoTracking().SingleAsync(item => item.Kind == "time");
            Assert.Equal(1, state.Prepared);
            Assert.True(state.LastId > 0);
            Assert.False((await service.StatusAsync("time", default)).Ready);
            while (!await service.PrepareBatchAsync("time", default)) { }
            var time = await service.StatusAsync("time", default);
            Assert.True(time.Ready); Assert.Equal(0, time.Pending); Assert.True(time.Unresolved >= 1);
            Assert.False((await service.StatusAsync("text", default)).Ready);
            while (!await service.PrepareBatchAsync("text", default)) { }
            var before = await resume.Locations.AsNoTracking().SingleAsync(item => item.Id == a.Id);
            await service.BeginAsync("text", true, default);
            Assert.True((await service.StatusAsync("time", default)).Ready);
            Assert.False((await service.StatusAsync("text", default)).Ready);
            while (!await service.PrepareBatchAsync("text", default)) { }
            var after = await resume.Locations.AsNoTracking().SingleAsync(item => item.Id == a.Id);
            Assert.Equal(before.HistoryTimeVersion, after.HistoryTimeVersion);
            Assert.Equal(before.CaptureLocalTimestamp, after.CaptureLocalTimestamp);
            AssertCanonicalEqual(before, after);
            await service.BeginAsync("time", true, default);
            var rebuilding = await service.StatusAsync("time", default);
            Assert.Equal(rebuilding.Total, rebuilding.Pending);
            Assert.Equal(0, rebuilding.Unresolved);
            Assert.Equal(rebuilding.Total, rebuilding.Pending + rebuilding.Resolved + rebuilding.Unresolved);
            Assert.True((await service.StatusAsync("text", default)).Ready);
            while (!await service.PrepareBatchAsync("time", default)) { }
            // A stale row below the durable ID checkpoint must still defeat readiness.
            await resume.Locations.Where(item => item.Id == a.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.HistoryTextVersion, "old"));
            Assert.False((await service.StatusAsync("text", default)).Ready);
            await service.BeginAsync("text", false, default);
            while (!await service.PrepareBatchAsync("text", default)) { }
            Assert.True((await service.StatusAsync("text", default)).Ready);
            await using (var missingIndex = await resume.Database.BeginTransactionAsync())
            {
                await resume.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Location_HistoryText\"");
                var unavailable = await service.StatusAsync("text", default);
                Assert.False(unavailable.IndexesReady); Assert.False(unavailable.Ready);
                await missingIndex.RollbackAsync();
            }
            Assert.True((await service.StatusAsync("text", default)).Ready);
        }
    }

    [PostgresFact]
    public async Task CancelledBatchLeavesRowsAndCheckpointResumableAndDoesNotResurrectDeletedRows()
    {
        var owner = await fixture.CreateUserAsync(); var row = Row(owner.Id);
        await using var db = fixture.CreateContext(); db.Add(row); await db.SaveChangesAsync();
        await ClearDerivedAsync(db, [row.Id]);
        var service = new LocationHistoryPreparation(db); await service.BeginAsync("time", true, default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync("time", cancelled.Token));
        var state = await db.HistoryPreparationStates.AsNoTracking().SingleAsync(item => item.Kind == "time");
        Assert.Equal(0, state.LastId); Assert.Equal(0, state.Prepared); Assert.False(state.Ready);
        Assert.Null(await db.Locations.Where(item => item.Id == row.Id).Select(item => item.HistoryTimeVersion).SingleAsync());
        await using (var delete = fixture.CreateContext()) await delete.Locations.Where(item => item.Id == row.Id).ExecuteDeleteAsync();
        while (!await service.PrepareBatchAsync("time", default)) { }
        Assert.False(await db.Locations.AnyAsync(item => item.Id == row.Id));
    }

    [PostgresFact]
    public async Task DerivedOnlyPreparationDoesNotBecomeCanonicalApiConflict()
    {
        var owner = await fixture.CreateUserAsync(); var row = Row(owner.Id);
        await using var editor = fixture.CreateContext(); editor.Add(row); await editor.SaveChangesAsync();
        await editor.Locations.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.HistoryTimeVersion, "old-rules"));
        await editor.Entry(row).ReloadAsync();
        var original = editor.Entry(row).OriginalValues.Clone();
        await using (var preparation = fixture.CreateContext())
        {
            await ClearDerivedAsync(preparation, [row.Id]);
            var service = new LocationHistoryPreparation(preparation); await service.BeginAsync("time", true, default);
            while (!await service.PrepareBatchAsync("time", default)) { }
        }
        await using var transaction = await editor.Database.BeginTransactionAsync();
        var current = await ApiLocationUpdate.RequireCurrentAsync(editor, row, original, default);
        Assert.Equal(LocationHistoryDerivation.TimeVersion, current.HistoryTimeVersion);
        original[nameof(Location.Notes)] = "a genuinely stale canonical edit";
        await Assert.ThrowsAsync<LocationUpdateConflictException>(() => ApiLocationUpdate.RequireCurrentAsync(editor, row, original, default));
    }

    [PostgresFact]
    public async Task PreparationReloadsConcurrentEditsAndDeletesAndRollsBackInterruptedBatch()
    {
        var owner = await fixture.CreateUserAsync(); var a = Row(owner.Id); var b = Row(owner.Id);
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(a, b); await seed.SaveChangesAsync(); await ClearDerivedAsync(seed, [a.Id, b.Id]);
            await new LocationHistoryPreparation(seed).BeginAsync("text", true, default);
        }
        var gate = new PreparationGate();
        await using (var worker = fixture.CreateContext(gate))
        {
            var service = new LocationHistoryPreparation(worker);
            var batch = service.PrepareBatchAsync("text", default);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using (var edit = fixture.CreateContext())
            {
                var current = await edit.Locations.SingleAsync(row => row.Id == a.Id);
                current.Notes = "<p>concurrent café</p>"; current.Address = "final address";
                await edit.SaveChangesAsync();
                await edit.Locations.Where(row => row.Id == b.Id).ExecuteDeleteAsync();
            }
            gate.Release.TrySetResult(); await batch;
            while (!await service.PrepareBatchAsync("text", default)) { }
        }
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.AsNoTracking().SingleAsync(row => row.Id == a.Id);
        Assert.Equal("CONCURRENT CAFE\nFINAL ADDRESS", saved.HistorySearchText);
        Assert.False(await verify.Locations.AnyAsync(row => row.Id == b.Id));
        await ClearDerivedAsync(verify, [a.Id]);
        await new LocationHistoryPreparation(verify).BeginAsync("time", true, default);
        await using (var interrupted = fixture.CreateContext(new InterruptDerivedWrite()))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocationHistoryPreparation(interrupted).PrepareBatchAsync("time", default));
        Assert.Null(await verify.Locations.Where(row => row.Id == a.Id).Select(row => row.HistoryTimeVersion).SingleAsync());
        var checkpoint = await verify.HistoryPreparationStates.AsNoTracking().SingleAsync(row => row.Kind == "time");
        Assert.Equal(0, checkpoint.LastId); Assert.Equal(0, checkpoint.Prepared);
        var resume = new LocationHistoryPreparation(verify);
        while (!await resume.PrepareBatchAsync("time", default)) { }
        Assert.True((await resume.StatusAsync("time", default)).Ready);
    }

    /// <summary>Pauses before the preparation's final-row reload so real independent writers can commit.</summary>
    private sealed class PreparationGate : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM \"Locations\"") && command.CommandText.Contains("FOR UPDATE"))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    /// <summary>Interrupts after an actual derived UPDATE to prove the transaction, not just entry cancellation.</summary>
    private sealed class InterruptDerivedWrite : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"Locations\"") && command.CommandText.Contains("HistoryTimeVersion"))
                throw new OperationCanceledException("Synthetic interruption after derived write.");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Simulates pre-migration rows without touching canonical columns.</summary>
    private static Task ClearDerivedAsync(ApplicationDbContext db, int[] ids) => db.Locations.Where(row => ids.Contains(row.Id))
        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.HistoryTimeVersion, (string?)null)
            .SetProperty(row => row.HistoryTimeStatus, (string?)null).SetProperty(row => row.HistoryTimeFailureCode, (string?)null)
            .SetProperty(row => row.CaptureLocalTimestamp, (DateTime?)null).SetProperty(row => row.CaptureTimeZoneId, (string?)null)
            .SetProperty(row => row.CaptureOffsetSeconds, (int?)null).SetProperty(row => row.HistorySearchText, (string?)null)
            .SetProperty(row => row.HistoryTextVersion, (string?)null));

    /// <summary>Canonical values are never restored from a preparation snapshot.</summary>
    private static void AssertCanonicalEqual(Location before, Location after)
    {
        Assert.Equal((before.UserId, before.IdempotencyKey, before.Timestamp, before.LocalTimestamp, before.TimeZoneId, before.Notes, before.Address),
            (after.UserId, after.IdempotencyKey, after.Timestamp, after.LocalTimestamp, after.TimeZoneId, after.Notes, after.Address));
        Assert.True(before.Coordinates.EqualsExact(after.Coordinates));
    }

    /// <summary>Small synthetic rows with distinct original clocks and retained source evidence.</summary>
    private static Location Row(string owner) => new()
    {
        UserId = owner, IdempotencyKey = Guid.NewGuid(), TimeZoneId = "UTC", Coordinates = new Point(139.76, 35.68) { SRID = 4326 },
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        LocalTimestamp = new DateTime(2025, 10, 10, 3, 0, 0, DateTimeKind.Utc), Notes = "<p>original café</p>", Source = "import"
    };
}
