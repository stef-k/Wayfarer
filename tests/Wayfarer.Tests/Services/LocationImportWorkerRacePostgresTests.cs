using Microsoft.EntityFrameworkCore;
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
    [PostgresFact]
    public async Task DeletionAfterFirstBatch_FencesSecondBatchAndReconcilesDeletion()
    {
        var seed = await SeedAsync(51, enrichmentRequested: true);
        var observer = new BlockingImportObserver(blockAfterCommittedBatch: true);
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
        var seed = await SeedAsync(2, enrichmentRequested: false);
        var observer = new BlockingImportObserver(blockBeforeTerminalPersistence: true);
        var scheduler = EmptyScheduler();
        await using var workerDb = fixture.CreateContext();
        var service = Service(workerDb, null, observer);
        var lifecycle = new LocationImportLifecycle(workerDb, scheduler.Object,
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
            var import = await pending.LocationImports.SingleAsync(x => x.Id == seed.ImportId);
            Assert.Equal(seed.DeletionAt, import.DeletionRequestedAtUtc);
            Assert.Equal(seed.Epoch, import.ExecutionEpoch);
            Assert.Equal(2, import.LastProcessedIndex);
            Assert.Equal(2, await pending.Locations.CountAsync(x => x.UserId == seed.UserId));
            Assert.DoesNotContain(pending.JobHistories, x => x.JobName == context.Object.JobDetail.Key.Name
                && x.Status == "Completed");
        }

        await ReconcileTwiceAsync(scheduler.Object);
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.Equal(2, await final.Locations.CountAsync(x => x.UserId == seed.UserId));
    }

    private async Task<Seed> SeedAsync(int count, bool enrichmentRequested)
    {
        var user = await fixture.CreateUserAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"wayfarer-511-worker-{Guid.NewGuid():N}");
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
        return new(user.Id, import.Id, import.ExecutionEpoch, path,
            new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
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

    private sealed record Seed(string UserId, int ImportId, int Epoch, string Path, DateTime DeletionAt);
}
