using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Util;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Uses the existing guarded PostgreSQL/staging/scheduler seams with fixture-owned account and files.</summary>
internal sealed class ManagedImportTestScope(PostgresImportTestFixture fixture) : IAsyncDisposable
{
    internal string Credential { get; } = "managed-fixture-" + Guid.NewGuid().ToString("N");
    internal ApplicationUser User { get; private set; } = null!;
    internal LocationImportStagedFiles Files => ImportStaging.Files;
    internal readonly Contexts Factory = new(fixture);
    internal readonly Mock<IScheduler> Scheduler = new();
    internal ManagedImportStorageGate Gate { get; private set; } = null!;
    internal ManagedImportUpload Upload { get; private set; } = null!;
    internal ManagedImportCleanup Cleanup { get; private set; } = null!;
    internal ManagedImportLifecycle Lifecycle { get; private set; } = null!;
    internal readonly HashSet<Guid> CustodyIds = [];

    internal async Task InitializeAsync()
    {
        User = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { UserId = User.Id, User = null!, Name = "fixture", TokenHash = ApiTokenService.HashToken(Credential) });
        await db.SaveChangesAsync();
        var data = Path.GetDirectoryName(Path.GetDirectoryName(Files.DirectoryPath))!;
        var storage = new StoragePaths(Options.Create(new StorageOptions
        { DataRoot = data, CacheRoot = data, LogRoot = data, TempRoot = data }), Mock.Of<IHostEnvironment>(host => host.ContentRootPath == data));
        Gate = new(storage, Options.Create(new ManagedImportOptions { LocalStoreQualified = true }));
        Scheduler.Setup(item => item.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Scheduler.Setup(item => item.GetJobDetail(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) => LocationImportReconciler.TryParseJob(key, out var id, out var epoch)
                ? LocationImportSchedulerKeys.BuildJob(id, epoch) : null);
        Scheduler.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TriggerKey key, CancellationToken _) => LocationImportReconciler.TryParseTrigger(key, out var id, out var epoch)
                ? LocationImportSchedulerKeys.BuildTrigger(id, epoch) : null);
        Scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Scheduler.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Upload = new(Factory, Files, Gate);
        Cleanup = new(Factory, Files, Gate, Scheduler.Object, NullLogger<ManagedImportCleanup>.Instance);
        Lifecycle = new(Factory, Files, Scheduler.Object, NullLogger<LocationImportLifecycle>.Instance, Cleanup);
    }

    internal async Task<MobileLocationImportUpload> AcceptAsync(string text = "Latitude,Longitude,TimestampUtc\n1,2,2026-10-01T00:00:00Z\n", string format = "csv", Guid? requestId = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var fingerprint = Fingerprint(bytes, format);
        var result = await Upload.AcceptAsync(User.Id, Credential, requestId ?? Guid.NewGuid(), fingerprint,
            UploadRequestPolicy.MaximumRequestBytes, new MemoryStream(bytes), default);
        CustodyIds.Add(result.Id);
        return await ReadAsync(result.Id);
    }

    internal static ManagedImportFingerprint Fingerprint(byte[] bytes, string format = "csv") => new(format,
        bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), false);

    internal async Task<MobileLocationImportUpload> ReadAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Set<MobileLocationImportUpload>().AsNoTracking().Include(row => row.Import).SingleAsync(row => row.Id == id);
    }

    internal async Task DueAsync(MobileLocationImportUpload row, ImportStatus? state = null)
    {
        await using var db = fixture.CreateContext();
        var stored = await db.Set<MobileLocationImportUpload>().Include(item => item.Import).SingleAsync(item => item.Id == row.Id);
        if (state is not null)
        {
            stored.Import!.Status = state;
            stored.Import.ProjectionPending = false;
            stored.Import.StopRequestedAtUtc = null;
        }
        stored.CleanupEligibleAtUtc = DateTime.UtcNow.AddHours(-25);
        stored.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        stored.RetryAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Upload.Observer = null;
        Cleanup.Observer = null;
        await using (var db = fixture.CreateContext())
        {
            foreach (var id in await db.Set<MobileLocationImportUpload>().Where(row => row.UserId == User.Id).Select(row => row.Id).ToListAsync())
                CustodyIds.Add(id);
            await using var transaction = await ManagedImportAuthority.BeginAsync(db, User.Id, default);
            await db.Users.Where(row => row.Id == User.Id).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        foreach (var id in CustodyIds)
        {
            await using (var db = fixture.CreateContext())
                await db.Set<MobileLocationImportUpload>().Where(row => row.Id == id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUtc, (DateTime?)null));
            await Cleanup.ProcessAsync(id, default);
        }
        await using var verification = fixture.CreateContext();
        if (await verification.Set<MobileLocationImportUpload>().AnyAsync(row => CustodyIds.Contains(row.Id)))
            throw new InvalidOperationException("Fixture-owned managed cleanup remains unresolved.");
        if (!await verification.Set<MobileLocationImportUpload>().AnyAsync())
        {
            using var storage = Gate.TryAcquire();
            if (storage is not null)
                await verification.Set<ManagedImportStore>().Where(row => row.Identity == storage.StoreIdentity).ExecuteDeleteAsync();
        }
    }

    internal sealed class Contexts(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
