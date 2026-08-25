using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves deletion fencing at real import batch and terminal persistence boundaries.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportWorkerRacePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(FailureAuthority.Current)]
    [InlineData(FailureAuthority.Stop)]
    [InlineData(FailureAuthority.NewEpoch)]
    public async Task FailureHistory_UsesFreshRelationalAuthority(FailureAuthority change)
    {
        await using var seed = await SeedAsync(50, enrichmentRequested: true);
        var observer = new FailingTerminalObserver();
        seed.ReleaseGates = observer.ReleaseAll;
        var handoff = new Mock<IImportEnrichmentHandoff>();
        var scheduler = EmptyScheduler();
        await using var workerDb = fixture.CreateContext();
        var job = new LocationImportJob(Service(workerDb, handoff.Object, observer),
            NullLogger<LocationImportJob>.Instance,
            new LocationImportLifecycle(new FixtureFactory(fixture), scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance, observer));
        var context = JobContext(seed.ImportId, seed.Epoch);

        var running = job.Execute(context.Object);
        await observer.BatchReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observer.ReleaseBatch();
        await observer.TerminalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change != FailureAuthority.Current)
        {
            await using var authority = fixture.CreateContext();
            var import = await authority.LocationImports.SingleAsync(x => x.Id == seed.ImportId);
            if (change == FailureAuthority.Stop)
            {
                import.Status = ImportStatus.Stopping;
                import.StopRequestedAtUtc = DateTime.UtcNow;
                import.ProjectionPending = true;
            }
            else
            {
                import.ExecutionEpoch++;
                import.ProjectionPending = false;
            }
            await authority.SaveChangesAsync();
        }
        observer.ReleaseTerminal();
        await running;

        await using var verification = fixture.CreateContext();
        await Listener(verification).JobWasExecuted(context.Object, null, CancellationToken.None);
        var stored = await verification.LocationImports.AsNoTracking().SingleAsync(x => x.Id == seed.ImportId);
        var history = await verification.JobHistories.SingleAsync(x => x.JobName == context.Object.JobDetail.Key.Name);
        if (change == FailureAuthority.Current)
        {
            Assert.Equal(LocationImportExecutionOutcome.Failed, context.Object.Result);
            Assert.Equal(ImportStatus.Failed, stored.Status);
            Assert.Equal("Failed", history.Status);
            Assert.Equal("Import processing failed.", stored.ErrorMessage);
        }
        else
        {
            Assert.True((LocationImportExecutionOutcome)context.Object.Result! is
                LocationImportExecutionOutcome.Cancelled or LocationImportExecutionOutcome.Stale);
            Assert.Equal("Cancelled", history.Status);
            Assert.DoesNotContain("sensitive-worker-detail", stored.ErrorMessage ?? string.Empty);
            Assert.Equal(change == FailureAuthority.Stop ? seed.Epoch : seed.Epoch + 1, stored.ExecutionEpoch);
        }
        Assert.Equal(50, await verification.Locations.CountAsync(x => x.UserId == seed.UserId));
        Assert.DoesNotContain("sensitive-worker-detail", history.Status);
        handoff.Verify(x => x.EnsureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await verification.JobHistories.Where(x => x.JobName == context.Object.JobDetail.Key.Name).ExecuteDeleteAsync();
    }

    [PostgresFact]
    public async Task DeletionAfterFirstBatch_FencesSecondBatchAndReconcilesDeletion()
    {
        await using var seed = await SeedAsync(51, enrichmentRequested: true);
        var observer = new BlockingImportObserver(blockAfterCommittedBatch: true);
        seed.ReleaseGates = observer.Release;
        var handoff = new Mock<IImportEnrichmentHandoff>();
        var scheduler = EmptyScheduler();
        LocationImportExecutionOutcome outcome;

        await using (var workerDb = fixture.CreateContext())
        {
            var worker = Service(workerDb, handoff.Object, observer);
            var running = worker.ProcessImportExecution(seed.ImportId, seed.Epoch, CancellationToken.None);
            await observer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await CommitDeletionIntentAsync(seed, expectedProgress: 50);
            observer.Release();
            outcome = await running;
        }

        Assert.Equal(LocationImportExecutionOutcome.Stale, outcome);
        await using (var pending = fixture.CreateContext())
        {
            var import = await pending.LocationImports.SingleAsync(x => x.Id == seed.ImportId);
            Assert.Equal(seed.DeletionAt, import.DeletionRequestedAtUtc);
            Assert.Equal(seed.Epoch, import.ExecutionEpoch);
            Assert.Equal(50, import.LastProcessedIndex);
            Assert.Equal(50, await pending.Locations.CountAsync(x => x.UserId == seed.UserId));
        }
        handoff.Verify(x => x.EnsureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await ReconcileTwiceAsync(scheduler.Object);
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.Equal(50, await final.Locations.CountAsync(x => x.UserId == seed.UserId));
    }

    [PostgresFact]
    public async Task DeletionAtTerminalPersistence_FencesCompletionAndHistoryThenReconcilesIdempotently()
    {
        await using var seed = await SeedAsync(2, enrichmentRequested: false);
        var observer = new BlockingImportObserver(blockBeforeTerminalPersistence: true);
        seed.ReleaseGates = observer.Release;
        var scheduler = EmptyScheduler();
        await using var workerDb = fixture.CreateContext();
        var service = Service(workerDb, null, observer);
        var lifecycle = new LocationImportLifecycle(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportLifecycle>.Instance, observer);
        var job = new LocationImportJob(service, NullLogger<LocationImportJob>.Instance, lifecycle);
        var context = JobContext(seed.ImportId, seed.Epoch);

        var running = job.Execute(context.Object);
        await observer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CommitDeletionIntentAsync(seed, expectedProgress: 2);
        observer.Release();
        await running;

        Assert.Equal(LocationImportExecutionOutcome.Stale, context.Object.Result);
        await using (var pending = fixture.CreateContext())
        {
            await Listener(pending).JobWasExecuted(context.Object, null, CancellationToken.None);
            var import = await pending.LocationImports.SingleAsync(x => x.Id == seed.ImportId);
            Assert.Equal(seed.DeletionAt, import.DeletionRequestedAtUtc);
            Assert.Equal(seed.Epoch, import.ExecutionEpoch);
            Assert.Equal(2, import.LastProcessedIndex);
            Assert.Equal(2, await pending.Locations.CountAsync(x => x.UserId == seed.UserId));
            var history = await pending.JobHistories.SingleAsync(x => x.JobName == context.Object.JobDetail.Key.Name);
            Assert.Equal("Cancelled", history.Status);
        }

        await ReconcileTwiceAsync(scheduler.Object);
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.Equal(2, await final.Locations.CountAsync(x => x.UserId == seed.UserId));
        await final.JobHistories.Where(x => x.JobName == context.Object.JobDetail.Key.Name).ExecuteDeleteAsync();
    }

    private async Task<Seed> SeedAsync(int count, bool enrichmentRequested)
    {
        var user = await fixture.CreateUserAsync();
        var root = Path.Combine(Path.GetTempPath(), "wayfarer-511-worker-races");
        Directory.CreateDirectory(root);
        var baseline = Directory.GetDirectories(root).Select(DirectorySnapshot.Capture).ToArray();
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "locations.csv");
        var rows = Enumerable.Range(0, count).Select(index =>
            $"{37 + index * .001},{23 + index * .001},2026-01-01T00:{index:00}:00Z");
        await File.WriteAllLinesAsync(path, ["Latitude,Longitude,TimestampUtc", .. rows]);
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = ImportStatus.InProgress, ExecutionEpoch = 11,
            TotalRecords = 0, LastProcessedIndex = 0, EnrichmentRequested = enrichmentRequested
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return new Seed(user.Id, import.Id, import.ExecutionEpoch, path,
            new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc), root, directory, baseline);
    }

    private async Task CommitDeletionIntentAsync(Seed seed, int expectedProgress)
    {
        await using var command = fixture.CreateContext();
        var import = await command.LocationImports.SingleAsync(x => x.Id == seed.ImportId);
        Assert.Equal(expectedProgress, import.LastProcessedIndex);
        import.Status = ImportStatus.Stopped;
        import.ProjectionPending = false;
        import.DeletionRequestedAtUtc = seed.DeletionAt;
        await command.SaveChangesAsync();
    }

    private LocationImportService Service(ApplicationDbContext db, IImportEnrichmentHandoff? handoff,
        ILocationImportLifecycleObserver observer) => new(db,
        new ReverseGeocodingService(new HttpClient(new RejectingHandler()), NullLogger<BaseApiController>.Instance),
        NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance),
        new SseService(), handoff, observer);

    private async Task ReconcileTwiceAsync(IScheduler scheduler)
    {
        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();
    }

    private static Mock<IScheduler> EmptyScheduler()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default)).ReturnsAsync([]);
        scheduler.Setup(x => x.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default)).ReturnsAsync([]);
        return scheduler;
    }

    private static Mock<IJobExecutionContext> JobContext(int importId, int epoch)
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(x => x.JobDetail).Returns(LocationImportSchedulerKeys.BuildJob(importId, epoch));
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
        context.SetupProperty(x => x.Result);
        return context;
    }

    private static JobExecutionListener Listener(ApplicationDbContext db)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(x => x.GetService(typeof(ApplicationDbContext))).Returns(db);
        provider.Setup(x => x.GetService(typeof(SseService))).Returns(new SseService());
        var scope = Mock.Of<IServiceScope>(x => x.ServiceProvider == provider.Object);
        return new JobExecutionListener(Mock.Of<IServiceScopeFactory>(x => x.CreateScope() == scope));
    }

    private sealed class BlockingImportObserver(bool blockAfterCommittedBatch = false,
        bool blockBeforeTerminalPersistence = false) : ILocationImportLifecycleObserver
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Reached => reached;
        internal void Release() => release.TrySetResult();
        public async Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token)
        { if (blockAfterCommittedBatch && processed == 50) { reached.TrySetResult(); await release.Task.WaitAsync(token); } }
        public async Task BeforeTerminalPersistenceAsync(int importId, int epoch,
            LocationImportExecutionOutcome outcome, CancellationToken token)
        { if (blockBeforeTerminalPersistence) { reached.TrySetResult(); await release.Task.WaitAsync(token); } }
    }

    private sealed class FailingTerminalObserver : ILocationImportLifecycleObserver
    {
        private readonly TaskCompletionSource releaseBatch = NewSignal();
        private readonly TaskCompletionSource releaseTerminal = NewSignal();
        internal TaskCompletionSource BatchReached { get; } = NewSignal();
        internal TaskCompletionSource TerminalReached { get; } = NewSignal();
        internal void ReleaseBatch() => releaseBatch.TrySetResult();
        internal void ReleaseTerminal() => releaseTerminal.TrySetResult();
        internal void ReleaseAll() { ReleaseBatch(); ReleaseTerminal(); }
        public async Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token)
        {
            if (processed != 50) return;
            BatchReached.TrySetResult();
            await releaseBatch.Task.WaitAsync(token);
            throw new InvalidOperationException("sensitive-worker-detail");
        }
        public async Task BeforeTerminalPersistenceAsync(int importId, int epoch,
            LocationImportExecutionOutcome outcome, CancellationToken token)
        {
            TerminalReached.TrySetResult();
            await releaseTerminal.Task.WaitAsync(token);
        }
        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public enum FailureAuthority { Current, Stop, NewEpoch }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Provider contact is forbidden."));
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken token = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Seed(string userId, int importId, int epoch, string path, DateTime deletionAt,
        string root, string directory, DirectorySnapshot[] baseline) : IAsyncDisposable
    {
        public string UserId { get; } = userId;
        public int ImportId { get; } = importId;
        public int Epoch { get; } = epoch;
        public string Path { get; } = path;
        public DateTime DeletionAt { get; } = deletionAt;
        internal Action? ReleaseGates { get; set; }

        public ValueTask DisposeAsync()
        {
            ReleaseGates?.Invoke();
            var resolvedRoot = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
            var resolvedDirectory = System.IO.Path.GetFullPath(directory);
            Assert.StartsWith(resolvedRoot, resolvedDirectory + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolvedDirectory)) Directory.Delete(resolvedDirectory, recursive: true);
            Assert.Equal(baseline.Length, Directory.GetDirectories(root).Length);
            Assert.All(baseline, snapshot => snapshot.AssertUnchanged());
            return ValueTask.CompletedTask;
        }
    }

    private sealed record DirectorySnapshot(string Path, DateTime LastWriteUtc, long FileBytes)
    {
        internal static DirectorySnapshot Capture(string path) => new(path,
            Directory.GetLastWriteTimeUtc(path), Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length));

        internal void AssertUnchanged()
        {
            Assert.True(Directory.Exists(Path));
            Assert.Equal(LastWriteUtc, Directory.GetLastWriteTimeUtc(Path));
            Assert.Equal(FileBytes, Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length));
        }
    }
}
