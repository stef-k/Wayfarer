using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Deletion suppression uses real old exports, native parsers, batch checkpoints and fresh worker contexts.</summary>
public sealed partial class LocationExportRoundTripPostgresTests
{
    /// <summary>Pre-deletion exports cannot restore deleted normal or singleton-zero keys; another owner remains independent.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv, false)]
    [InlineData(LocationImportFileType.Csv, true)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, false)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, true)]
    public async Task PreDeletionExport_SuppressesDeletedAndRetainsDuplicate(LocationImportFileType format, bool zero)
    {
        var owner = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var deleted = CreateLocation(owner.Id, zero ? Guid.Empty : Guid.NewGuid());
        var retained = CreateLocation(owner.Id, Guid.NewGuid());
        retained.Timestamp = retained.Timestamp.AddHours(1);
        await using (var db = fixture.CreateContext())
        {
            db.Locations.AddRange(deleted, retained);
            await db.SaveChangesAsync();
        }
        var oldExport = await ExportAsync(owner.Id, format);
        await using (var db = fixture.CreateContext())
            await LocationDeletionFence.DeleteAsync(db, owner.Id, [deleted.Id], null, CancellationToken.None);
        var result = await ImportAsync(owner.Id, format, oldExport);
        Assert.Equal((2, 1, 1), (result.LastProcessedIndex, result.SkippedDeleted, result.SkippedDuplicates));
        await using var verify = fixture.CreateContext();
        Assert.Equal(retained.Id, (await verify.Locations.SingleAsync(row => row.UserId == owner.Id)).Id);
        Assert.Equal(0, result.UnclassifiedActivityCount);
        var independent = await ImportAsync(other.Id, format, oldExport);
        Assert.Equal((2, 0, 0), (independent.LastProcessedIndex, independent.SkippedDeleted, independent.SkippedDuplicates));
        Assert.Equal(2, await verify.Locations.CountAsync(row => row.UserId == other.Id));
    }

    /// <summary>Rows, Activity effects, separate suppression/duplicate counts and ordinal progress commit or roll back together.</summary>
    [PostgresTheory]
    [InlineData("normal")]
    [InlineData("rollback")]
    [InlineData("lost-commit-ack")]
    public async Task MixedBatch_AccountsAtomicallyAndRecovers(string failure)
    {
        var user = await fixture.CreateUserAsync();
        var deleted = CreateLocation(user.Id, Guid.NewGuid());
        var retained = CreateLocation(user.Id, Guid.NewGuid());
        var accepted = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            db.Locations.AddRange(deleted, retained);
            await db.SaveChangesAsync();
            await LocationDeletionFence.DeleteAsync(db, user.Id, [deleted.Id], null, CancellationToken.None);
        }
        var names = new[] { activityPrefix + "suppressed", activityPrefix + "duplicate", activityPrefix + "accepted" };
        var keys = new[] { deleted.IdempotencyKey!.Value, deleted.IdempotencyKey.Value,
            retained.IdempotencyKey!.Value, accepted, accepted };
        var csv = CsvWithKeys(keys, index => index < 2 ? names[0] : index == 2 || index == 4 ? names[1] : names[2]);
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllBytesAsync(path, csv);
            var import = await SeedDeletionImportAsync(user.Id, path);
            var fault = failure == "rollback" ? (IInterceptor)new FailCheckpoint() : new LostBatchCommitAcknowledgement();
            var worker = DeletionWorker(failure == "normal" ? [] : [fault]);
            var outcome = await worker.ProcessImportExecution(import.Id, 7, CancellationToken.None);
            if (failure == "rollback")
            {
                Assert.Equal(LocationImportExecutionOutcome.Failed, outcome);
                await using var failed = fixture.CreateContext();
                var checkpoint = await failed.LocationImports.AsNoTracking().SingleAsync(row => row.Id == import.Id);
                Assert.Equal((0, 0, 0), (checkpoint.LastProcessedIndex, checkpoint.SkippedDeleted, checkpoint.SkippedDuplicates));
                Assert.False(await failed.Locations.AnyAsync(row => row.UserId == user.Id && row.IdempotencyKey == accepted));
                Assert.False(await failed.ActivityTypes.AnyAsync(row => names.Contains(row.Name)));
                outcome = await DeletionWorker().ProcessImportExecution(import.Id, 7, CancellationToken.None);
            }
            Assert.Equal(LocationImportExecutionOutcome.Completed, outcome);
            await using var verify = fixture.CreateContext();
            var saved = await verify.LocationImports.AsNoTracking().SingleAsync(row => row.Id == import.Id);
            Assert.Equal((5, 2, 2, 0), (saved.LastProcessedIndex, saved.SkippedDeleted, saved.SkippedDuplicates, saved.UnclassifiedActivityCount));
            Assert.Equal(2, await verify.Locations.CountAsync(row => row.UserId == user.Id));
            Assert.Equal(names[2], (await verify.Locations.Include(row => row.ActivityType)
                .SingleAsync(row => row.UserId == user.Id && row.IdempotencyKey == accepted)).ActivityType!.Name);
            Assert.False(await verify.ActivityTypes.AnyAsync(row => row.Name == names[0] || row.Name == names[1]));
            Assert.All(await verify.Locations.Where(row => row.UserId == user.Id && row.IdempotencyKey == accepted).ToListAsync(),
                row => Assert.Null(row.PresenceObservedAtUtc));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A new worker resumes after the 50-row commit and suppresses the deleted key when it recurs at ordinal 51.</summary>
    [PostgresFact]
    public async Task FiftyRowCheckpoint_DeletionSurvivesInterruptedWorkerAndResume()
    {
        var user = await fixture.CreateUserAsync();
        var keys = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        keys.Add(keys[0]);
        keys.Add(Guid.NewGuid());
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllBytesAsync(path, CsvWithKeys(keys));
            var import = await SeedDeletionImportAsync(user.Id, path);
            var observer = new AfterCommittedBatch(async () =>
            {
                await using var db = fixture.CreateContext();
                var id = await db.Locations.Where(row => row.UserId == user.Id && row.IdempotencyKey == keys[0])
                    .Select(row => row.Id).SingleAsync();
                await LocationDeletionFence.DeleteAsync(db, user.Id, [id], null, CancellationToken.None);
                throw new OperationCanceledException();
            });
            Assert.Equal(LocationImportExecutionOutcome.Cancelled,
                await DeletionWorker(observer: observer).ProcessImportExecution(import.Id, 7, CancellationToken.None));
            await using (var checkpoint = fixture.CreateContext())
            {
                var saved = await checkpoint.LocationImports.AsNoTracking().SingleAsync(row => row.Id == import.Id);
                Assert.Equal((52, 50, 0, 0), (saved.TotalRecords, saved.LastProcessedIndex, saved.SkippedDeleted, saved.SkippedDuplicates));
                Assert.Equal(49, await checkpoint.Locations.CountAsync(row => row.UserId == user.Id));
            }
            await DeletionWorker().ProcessImport(import.Id, 7, CancellationToken.None);
            await using var verify = fixture.CreateContext();
            var completed = await verify.LocationImports.AsNoTracking().SingleAsync(row => row.Id == import.Id);
            Assert.Equal(ImportStatus.Completed, completed.Status);
            Assert.Equal((52, 1, 0), (completed.LastProcessedIndex, completed.SkippedDeleted, completed.SkippedDuplicates));
            Assert.Equal(50, await verify.Locations.CountAsync(row => row.UserId == user.Id));
            Assert.False(await verify.Locations.AnyAsync(row => row.UserId == user.Id && row.IdempotencyKey == keys[0]));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Builds native CSV input without changing parsers or manufacturing destination Location IDs.</summary>
    private static byte[] CsvWithKeys(IReadOnlyList<Guid> keys, Func<int, string>? activity = null) => Encoding.UTF8.GetBytes(
        "Latitude,Longitude,TimestampUtc,IdempotencyKey,Activity\r\n" + string.Join("\r\n", keys.Select((key, index) =>
            $"40.1,22.2,{DateTime.UnixEpoch.AddHours(index):O},{key:D},{activity?.Invoke(index)}")) + "\r\n");

    /// <summary>Persists normal import authority before entering a fault-controlled worker context.</summary>
    private async Task<LocationImport> SeedDeletionImportAsync(string userId, string path)
    {
        await using var db = fixture.CreateContext();
        var import = new LocationImport { UserId = userId, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = ImportStatus.InProgress, ExecutionEpoch = 7, TotalRecords = 0, LastProcessedIndex = 0 };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    /// <summary>Uses fresh production batch contexts and the established observer seam for context/restart recovery.</summary>
    private LocationImportService DeletionWorker(IInterceptor[]? interceptors = null, ILocationImportLifecycleObserver? observer = null) =>
        new(ImportStaging.Files, new DeletionContexts(fixture, interceptors ?? []),
            new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance),
            new SseService(), null, observer ?? NullLocationImportLifecycleObserver.Instance);

    /// <summary>Restricts injected faults to this worker's fixture-owned contexts.</summary>
    private sealed class DeletionContexts(PostgresImportTestFixture fixture, IInterceptor[] interceptors) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext(interceptors);
    }

    /// <summary>Fails before the atomic final checkpoint save, after provisional Location and Activity writes.</summary>
    private sealed class FailCheckpoint : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LocationImport>().Any(entry => entry.Entity.LastProcessedIndex > 0))
                throw new DbUpdateException("Synthetic checkpoint failure.");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Loses only the first observed COMMIT reply; recovery must read the atomic checkpoint rather than replay.</summary>
    private sealed class LostBatchCommitAcknowledgement : DbTransactionInterceptor
    {
        private int _commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => Interlocked.Increment(ref _commits) == 1
            ? Task.FromException(new IOException("Synthetic lost commit acknowledgement.")) : Task.CompletedTask;
    }

    /// <summary>Interrupts only after a durable batch; no production stop or generation authority is bypassed.</summary>
    private sealed class AfterCommittedBatch(Func<Task> after) : ILocationImportLifecycleObserver
    {
        public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token) => after();
        public Task BeforeTerminalPersistenceAsync(int importId, int epoch, LocationImportExecutionOutcome outcome,
            CancellationToken token) => Task.CompletedTask;
    }
}
