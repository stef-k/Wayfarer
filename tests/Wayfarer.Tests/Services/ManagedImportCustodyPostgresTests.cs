using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises real publication/cleanup/OS exclusion, without a second importer or storage harness.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ManagedImportCustodyPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task SameRequestConcurrencyAndLostAcceptanceReply_KeepOneAcceptedJob()
    {
        await using var scope = await ScopeAsync();
        var request = Guid.NewGuid();
        var bytes = "Latitude,Longitude,TimestampUtc\n"u8.ToArray();
        var fingerprint = ManagedImportTestScope.Fingerprint(bytes);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return (await scope.Upload.AcceptAsync(scope.User.Id, scope.Credential,
                request, fingerprint, UploadRequestPolicy.MaximumRequestBytes, new MemoryStream(bytes), default)).Id; }
            catch (ManagedImportException failure) when (failure.Status == 503) { return (Guid?)null; }
        }));
        var visible = await new ManagedImportStatus(scope.Factory).UploadAsync(scope.User.Id, request, default);
        Assert.NotNull(visible!.ImportId);
        Assert.All(results.Where(result => result.HasValue), result => Assert.Equal(visible.Id, result));
        scope.Upload.Observer = new LostReply();
        var failure = await Assert.ThrowsAsync<IOException>(() => scope.AcceptAsync(requestId: Guid.NewGuid()));
        Assert.Equal("fixture lost acceptance reply", failure.Message);
        await using var db = fixture.CreateContext();
        var rows = await db.Set<MobileLocationImportUpload>().Where(row => row.UserId == scope.User.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ManagedImportFileState.Retained, row.FileState));
        Assert.Equal(2, await db.LocationImports.CountAsync(row => row.UserId == scope.User.Id));
        var accepted = rows.Single(row => row.RequestId != request);
        var replay = await scope.Upload.AcceptAsync(scope.User.Id, scope.Credential, accepted.RequestId!.Value,
            new(accepted.Format!, accepted.ByteLength!.Value, accepted.Sha256!, false), 0, new ThrowingBody(), default);
        Assert.False(replay.Created);
        Assert.Equal(accepted.Id, replay.Id);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedOrInvalidUpload_ClaimsBeforeRemovalAndRetriesFreshGeneration(bool integrity)
    {
        await using var scope = await ScopeAsync();
        var request = Guid.NewGuid();
        var bytes = "Latitude,Longitude,TimestampUtc\n"u8.ToArray();
        var fingerprint = ManagedImportTestScope.Fingerprint(bytes);
        Stream body = integrity ? new MemoryStream("other bytes"u8.ToArray()) : new ThrowingBody();
        await Assert.ThrowsAnyAsync<Exception>(() => scope.Upload.AcceptAsync(scope.User.Id, scope.Credential,
            request, fingerprint, UploadRequestPolicy.MaximumRequestBytes, body, default));
        var row = (await new ManagedImportStatus(scope.Factory).UploadAsync(scope.User.Id, request, default))!;
        Assert.Null(row.ImportId);
        Assert.Equal(202, ManagedImportStatus.HttpStatus(row));
        Assert.Equal(ManagedImportFileState.CleanupClaimed, row.FileState);
        Assert.Equal(bytes.Length, row.ReservedBytes);
        var oldPath = row.FileReference;
        await scope.Cleanup.ProcessAsync(row.Id, default);
        Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
        var retry = await scope.Upload.AcceptAsync(scope.User.Id, scope.Credential, request,
            fingerprint, UploadRequestPolicy.MaximumRequestBytes, new MemoryStream(bytes), default);
        var current = await scope.ReadAsync(retry.Id);
        Assert.Equal(2, current.Generation);
        Assert.NotEqual(oldPath, current.FileReference);
        Assert.NotNull(current.ImportId);
        await Assert.ThrowsAsync<ManagedImportException>(() => scope.Upload.FirstOpenAsync(row, default));
    }

    [PostgresFact]
    public async Task R3_KilledUploaderSessionBeforeFirstOpen_CannotCreateAfterFinalizedRemoval()
    {
        await using var scope = await ScopeAsync();
        var observer = new BeforeOpen();
        scope.Upload.Observer = observer;
        var request = Guid.NewGuid();
        var upload = scope.AcceptAsync(requestId: request);
        var backend = await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = (await new ManagedImportStatus(scope.Factory).UploadAsync(scope.User.Id, request, default))!;
        await using (var killer = fixture.CreateContext())
            Assert.True(await killer.Database.SqlQuery<bool>($"SELECT pg_terminate_backend({backend}) AS \"Value\"").SingleAsync());
        await scope.DueAsync(row);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        var pending = await scope.ReadAsync(row.Id);
        Assert.Equal(ManagedImportFileState.CleanupClaimed, pending.FileState);
        Assert.Equal(row.ReservedBytes, pending.ReservedBytes);
        Assert.NotNull(pending.FileReference);
        observer.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => upload);
        await scope.DueAsync(pending);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        var removed = await scope.ReadAsync(row.Id);
        Assert.Equal(ManagedImportFileState.Removed, removed.FileState);
        Assert.Null(removed.ImportId);
        Assert.True(scope.Files.TryResolve(row.FileReference, out var path));
        Assert.False(File.Exists(path));
        scope.Upload.Observer = null;
        await Assert.ThrowsAsync<ManagedImportException>(() => scope.Upload.FirstOpenAsync(row, default));
        Assert.False(File.Exists(path));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedFileCleanup_CrashOrFilesystemFailureRetainsReservationAndConverges(bool filesystemFailure)
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        await scope.DueAsync(row, ImportStatus.Completed);
        Assert.True(scope.Files.TryResolve(row.FileReference, out var path));
        if (filesystemFailure) { File.Delete(path); Directory.CreateDirectory(path); }
        else scope.Cleanup.Observer = new UnlinkCrash();
        await scope.Cleanup.ProcessAsync(row.Id, default);
        var pending = await scope.ReadAsync(row.Id);
        Assert.Equal(ManagedImportFileState.CleanupClaimed, pending.FileState);
        Assert.Equal(row.ReservedBytes, pending.ReservedBytes);
        Assert.Equal(200, ManagedImportStatus.HttpStatus(pending));
        Assert.NotNull(pending.Import);
        if (filesystemFailure) Directory.Delete(path);
        scope.Cleanup.Observer = null;
        await scope.DueAsync(row);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        var removed = await scope.ReadAsync(row.Id);
        Assert.Equal(ManagedImportFileState.Removed, removed.FileState);
        Assert.Equal(0, removed.ReservedBytes);
        Assert.Equal(ImportStatus.Completed, removed.Import!.Status);
        var fingerprint = new ManagedImportFingerprint(row.Format!, row.ByteLength!.Value, row.Sha256!, false);
        var replay = await scope.Upload.AcceptAsync(scope.User.Id, scope.Credential, row.RequestId!.Value,
            fingerprint, 0, new ThrowingBody(), default);
        Assert.Equal(row.Id, replay.Id);
        Assert.False(replay.Created);
    }

    [PostgresFact]
    public async Task AccountDeletion_ErasesPersonalFactsButRetainsUnownedFileEvidenceUntilCleanup()
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        await using (var db = fixture.CreateContext())
        {
            await using var transaction = await ManagedImportAuthority.BeginAsync(db, scope.User.Id, default);
            await db.Users.Where(user => user.Id == scope.User.Id).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        var erased = await scope.ReadAsync(row.Id);
        Assert.Null(erased.UserId);
        Assert.Null(erased.RequestId);
        Assert.Null(erased.Sha256);
        Assert.Null(erased.AcceptedAtUtc);
        Assert.Null(erased.ImportId);
        Assert.Equal(row.FileReference, erased.FileReference);
        Assert.Equal(row.ReservedBytes, erased.ReservedBytes);
        Assert.Equal("account_deleted", erased.CleanupReason);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Set<MobileLocationImportUpload>().AnyAsync(item => item.Id == row.Id));
    }

    [PostgresFact]
    public async Task DatabaseConstraints_ProtectFingerprintReservationsAndExecutionSlots()
    {
        await using var scope = await ScopeAsync();
        var first = await scope.AcceptAsync();
        var second = await scope.AcceptAsync();
        _ = await scope.AcceptAsync();
        var quota = await Assert.ThrowsAsync<ManagedImportException>(() => scope.AcceptAsync());
        Assert.Equal("managed_file_limit", quota.Code);
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MobileLocationImportUpload\" SET \"Sha256\" = {new string('a', 64)} WHERE \"Id\" = {first.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MobileLocationImportUpload" ("Id", "UserId", "RequestId", "Format", "ByteLength", "Sha256", "EnrichmentRequested",
                "CreatedAtUtc", "Generation", "FileReference", "FileState", "ReservedBytes", "VerifiedBytes", "OwnerByteLimit", "LastProcessedRecords", "LastExecutionEpoch")
            VALUES ({Guid.NewGuid()}, {scope.User.Id}, {Guid.NewGuid()}, 'csv', 1, {new string('b', 64)}, false,
                clock_timestamp(), 1, {LocationImportStagedFiles.CreateReference(Wayfarer.Models.Enums.LocationImportFileType.Csv)},
                'Uploading', 1, 0, 314572800, 0, 0)
            """));
        await scope.Lifecycle.StartAsync(scope.User.Id, first.ImportId!.Value, 1, scope.Credential, default);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"LocationImports\" SET \"Status\" = 'In Progress', \"ExecutionEpoch\" = 1 WHERE \"Id\" = {second.ImportId}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"MobileLocationImportUpload\" WHERE \"Id\" = {first.Id}"));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownReservationOrAcceptanceCommit_RetainsCustodyUntilPrimaryDatabaseResolution(bool acceptance)
    {
        await using var scope = await ScopeAsync();
        var factory = new UncertainContexts(fixture, new LostCommit(acceptance));
        var uploader = new ManagedImportUpload(factory, scope.Files, scope.Gate);
        var bytes = "Latitude,Longitude,TimestampUtc\n"u8.ToArray();
        var request = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => uploader.AcceptAsync(scope.User.Id, scope.Credential,
            request, ManagedImportTestScope.Fingerprint(bytes), UploadRequestPolicy.MaximumRequestBytes, new MemoryStream(bytes), default));
        var row = (await new ManagedImportStatus(scope.Factory).UploadAsync(scope.User.Id, request, default))!;
        Assert.Equal(bytes.Length, row.ReservedBytes);
        Assert.Equal(acceptance ? ManagedImportFileState.Retained : ManagedImportFileState.Uploading, row.FileState);
        Assert.Equal(acceptance, row.AcceptedAtUtc.HasValue);
        Assert.True(scope.Files.TryResolve(row.FileReference, out var path));
        Assert.Equal(acceptance, File.Exists(path));
        if (!acceptance)
        {
            await scope.DueAsync(row);
            await scope.Cleanup.ProcessAsync(row.Id, default);
            Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
        }
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupOrRevocationBeforePublisher_FencesClosedStaleUploaderAndRetainsRequestForRetry(bool revoked)
    {
        await using var scope = await ScopeAsync();
        var observer = new ClosedUpload();
        scope.Upload.Observer = observer;
        var request = Guid.NewGuid();
        var upload = scope.AcceptAsync(requestId: request);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = (await new ManagedImportStatus(scope.Factory).UploadAsync(scope.User.Id, request, default))!;
        try
        {
            if (revoked)
            {
                await using var db = fixture.CreateContext();
                await db.ApiTokens.Where(item => item.UserId == scope.User.Id).ExecuteDeleteAsync();
            }
            else
            {
                await scope.DueAsync(row);
                await scope.Cleanup.ProcessAsync(row.Id, default);
                Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
            }
        }
        finally { observer.Release.TrySetResult(); }
        var failure = await Assert.ThrowsAsync<ManagedImportException>(() => upload);
        Assert.Equal(revoked ? 401 : 409, failure.Status);
        Assert.Null((await scope.ReadAsync(row.Id)).ImportId);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
    }

    private sealed class ClosedUpload : IManagedImportObserver
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AfterCloseAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    /// <summary>Throws after the actual PostgreSQL commit, rather than pretending a failed save proves rollback.</summary>
    private sealed class LostCommit(bool acceptance) : DbTransactionInterceptor
    {
        private int fired;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            var db = (ApplicationDbContext)eventData.Context!;
            if (db.ChangeTracker.Entries<MobileLocationImportUpload>().Any(entry => acceptance
                ? entry.Entity.AcceptedAtUtc.HasValue : entry.Entity.FileState == ManagedImportFileState.Uploading)
                && Interlocked.Exchange(ref fired, 1) == 0) throw new IOException("fixture lost commit acknowledgement");
            return Task.CompletedTask;
        }
    }

    private sealed class UncertainContexts(PostgresImportTestFixture fixture, LostCommit interceptor) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext(interceptor);
    }

    [PostgresFact]
    public async Task LinuxGate_ExcludesAnIndependentProcessAndReleasesOnDescriptorClose()
    {
        await using var scope = await ScopeAsync();
        var path = Path.Combine(Path.GetDirectoryName(scope.Files.DirectoryPath)!, ".location-import.lock");
        using (var lease = scope.Gate.TryAcquire())
        {
            Assert.NotNull(lease);
            Assert.Equal(1, await ProbeAsync(path));
        }
        Assert.Equal(0, await ProbeAsync(path));
    }

    private static async Task<int> ProbeAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("flock") { UseShellExecute = false };
        start.ArgumentList.Add("--nonblock");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("true");
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return process.ExitCode;
    }

    private async Task<ManagedImportTestScope> ScopeAsync()
    {
        var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        return scope;
    }

    private sealed class LostReply : IManagedImportObserver
    {
        public Task AfterAcceptanceAsync(CancellationToken token) => throw new IOException("fixture lost acceptance reply");
    }

    private sealed class UnlinkCrash : IManagedImportObserver
    {
        public Task AfterUnlinkAsync(CancellationToken token) => throw new IOException("fixture crash after unlink");
    }

    private sealed class BeforeOpen : IManagedImportObserver
    {
        internal readonly TaskCompletionSource<int> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task BeforeFirstOpenAsync(ApplicationDbContext db, CancellationToken token)
        {
            var backend = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(token);
            Entered.TrySetResult(backend);
            await Release.Task.WaitAsync(token);
        }
    }

    /// <summary>Proves replay does not inspect or consume new transport bytes.</summary>
    internal sealed class ThrowingBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("fixture interrupted body");
    }
}
