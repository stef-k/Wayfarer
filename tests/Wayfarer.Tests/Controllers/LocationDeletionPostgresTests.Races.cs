using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Point = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Tests.Controllers;

/// <summary>Observes actual PostgreSQL advisory-lock waiters, atomic rollback and account erasure.</summary>
public sealed partial class LocationDeletionPostgresTests
{
    /// <summary>Both SQL orderings serialize API/import writes with deletion, including an ingest that already missed its key.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccountFence_OrdersActualBlockedApiAndImport(bool import, bool createFirst)
    {
        var user = await fixture.CreateUserAsync();
        var key = Guid.NewGuid();
        var path = ImportStaging.TempFile();
        await File.WriteAllTextAsync(path, $"Latitude,Longitude,TimestampUtc,IdempotencyKey\r\n10,20,2026-01-01T00:00:00Z,{key:D}\r\n");
        var insert = new BeforeInsertPause();
        var commit = new BeforeCommitPause();
        await using var deletionDb = fixture.CreateContext(createFirst ? [] : [commit]);
        await deletionDb.Database.OpenConnectionAsync();
        var deletionPid = ((NpgsqlConnection)deletionDb.Database.GetDbConnection()).ProcessID;
        Task<List<int>>? deleting = null;
        var fence = new BeforeIngestFence(async () =>
        {
            if (createFirst) return;
            await using var seed = fixture.CreateContext();
            seed.Locations.Add(RaceLocation(user.Id, key));
            await seed.SaveChangesAsync();
            deleting = LocationDeletionFence.DeleteAsync(deletionDb, user.Id, null, null, CancellationToken.None);
            await commit.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        IInterceptor[] interceptors = [fence, insert];
        await using var intakeDb = fixture.CreateContext(interceptors);
        var events = new Mock<SseService>(MockBehavior.Strict);
        var controller = ApiLocationAdmissionTests.Controller(intakeDb, new ApiWorkAdmission(64, 8), sse: events.Object);
        controller.Request.Headers.Authorization = $"Bearer {user.Id}";
        controller.Request.Headers["Idempotency-Key"] = key.ToString();
        int importId;
        await using (var seed = fixture.CreateContext())
        {
            seed.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "race", Token = user.Id });
            var row = new LocationImport { UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7, TotalRecords = 0, LastProcessedIndex = 0 };
            seed.LocationImports.Add(row);
            await seed.SaveChangesAsync();
            importId = row.Id;
        }
        IActionResult? apiResult = null;
        LocationImportExecutionOutcome? importResult = null;
        async Task Ingest()
        {
            if (import)
            {
                var worker = new LocationImportService(ImportStaging.Files, new RaceContexts(fixture, interceptors),
                    new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                    NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
                importResult = await worker.ProcessImportExecution(importId, 7, CancellationToken.None);
            }
            else apiResult = await controller.CheckIn(ApiLocationAdmissionTests.Dto());
        }
        // Delete-first must let ingestion reach its fence; create-first pauses the provisional insert while holding it.
        if (!createFirst) insert.Release.TrySetResult();
        var ingesting = Ingest();
        try
        {
            if (createFirst)
            {
                await insert.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                deleting = LocationDeletionFence.DeleteAsync(deletionDb, user.Id, null, null, CancellationToken.None);
                await AssertAdvisoryWaiterAsync(deletionPid);
                insert.Release.TrySetResult();
            }
            else
            {
                var intakePid = await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await AssertAdvisoryWaiterAsync(intakePid);
                commit.Release.TrySetResult();
            }
            await ingesting.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(await deleting!.WaitAsync(TimeSpan.FromSeconds(10)));
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.Locations.AnyAsync(row => row.UserId == user.Id));
            Assert.True(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == user.Id && row.IdempotencyKey == key));
            if (import)
            {
                Assert.Equal(LocationImportExecutionOutcome.Completed, importResult);
                var progress = await verify.LocationImports.AsNoTracking().SingleAsync(row => row.Id == importId);
                Assert.Equal((1, createFirst ? 0 : 1, 0), (progress.LastProcessedIndex, progress.SkippedDeleted, progress.SkippedDuplicates));
            }
            else Assert.Equal(410, Assert.IsType<ObjectResult>(apiResult).StatusCode);
            events.VerifyNoOtherCalls();
        }
        finally
        {
            insert.Release.TrySetResult();
            commit.Release.TrySetResult();
            await ingesting.WaitAsync(TimeSpan.FromSeconds(10));
            if (deleting != null) await deleting.WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(path);
        }
    }

    /// <summary>Unknown IDs create no markers; failed deletion rolls back both facts; account erasure removes minimal metadata.</summary>
    [PostgresFact]
    public async Task DeletionRollbackUnknownIdsAndAccountErasure_AreAtomic()
    {
        var user = await fixture.CreateUserAsync();
        var keyed = RaceLocation(user.Id, Guid.NewGuid());
        var keyless = RaceLocation(user.Id, null);
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.AddRange(keyed, keyless);
            await seed.SaveChangesAsync();
            Assert.Empty(await LocationDeletionFence.DeleteAsync(seed, user.Id, [int.MaxValue], null, CancellationToken.None));
        }
        await using (var failing = fixture.CreateContext(new FailRemoval()))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocationDeletionFence.DeleteAsync(failing, user.Id, null, null, CancellationToken.None));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set($"lastLocation_{user.Id}", keyed);
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.Locations.CountAsync(row => row.UserId == user.Id));
        Assert.False(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == user.Id));
        Assert.Equal(2, (await LocationDeletionFence.DeleteAsync(verify, user.Id, null, cache, CancellationToken.None)).Count);
        Assert.False(cache.TryGetValue($"lastLocation_{user.Id}", out _));
        Assert.Single(await verify.Set<LocationDeletionMarker>().Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Equal(new[] { "IdempotencyKey", "UserId" }, verify.Model.FindEntityType(typeof(LocationDeletionMarker))!
            .GetProperties().Select(property => property.Name).Order());
        await verify.Users.Where(row => row.Id == user.Id).ExecuteDeleteAsync();
        Assert.False(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == user.Id));
    }

    /// <summary>Uses PostgreSQL's lock table, rather than elapsed timing, to prove the second transaction is waiting.</summary>
    private async Task AssertAdvisoryWaiterAsync(int pid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid = @pid AND locktype = 'advisory' AND NOT granted)";
        command.Parameters.AddWithValue("pid", pid);
        while (!(bool)(await command.ExecuteScalarAsync(deadline.Token))!)
            await Task.Delay(10, deadline.Token);
    }

    /// <summary>Supplies synthetic history without triggering enrichment or observation effects.</summary>
    private static Location RaceLocation(string userId, Guid? key) => new() { UserId = userId, IdempotencyKey = key,
        Coordinates = new Point(20, 10) { SRID = 4326 }, TimeZoneId = "UTC", Timestamp = DateTime.UnixEpoch,
        LocalTimestamp = DateTime.UnixEpoch };

    /// <summary>Shares only deterministic observation controls across the worker's independent contexts.</summary>
    private sealed class RaceContexts(PostgresImportTestFixture fixture, IInterceptor[] interceptors) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext(interceptors);
    }

    /// <summary>Exposes the actual ingestion backend before its first account lock, after optional deletion preparation.</summary>
    private sealed class BeforeIngestFence(Func<Task> prepare) : DbCommandInterceptor
    {
        internal TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(", 838)", StringComparison.Ordinal) && Interlocked.Increment(ref _count) == 1)
            {
                await prepare();
                Entered.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            }
            return result;
        }
    }

    /// <summary>Holds a provisional insert under the account lock until deletion is observed waiting.</summary>
    private sealed class BeforeInsertPause : SaveChangesInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<Location>().Any(entry => entry.State == EntityState.Added))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>Holds deletion intent and row removal uncommitted while ingestion reaches the same PostgreSQL lock.</summary>
    private sealed class BeforeCommitPause : DbTransactionInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    /// <summary>Fails after marker insertion but before Location removal to exercise transaction rollback.</summary>
    private sealed class FailRemoval : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic Location removal failure.");
            return ValueTask.FromResult(result);
        }
    }
}
