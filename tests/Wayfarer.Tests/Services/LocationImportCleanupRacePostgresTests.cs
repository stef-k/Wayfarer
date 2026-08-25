using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
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

/// <summary>Proves bounded cleanup cancellation and queued-worker admission after projection deletion.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportCleanupRacePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuartzCleanupCancellation_RetainsDeletionIntentAndRetryConverges(bool removeOneFirst)
    {
        var seed = await SeedCompletedAsync();
        var commandStarted = DateTime.UtcNow;
        var keys = new HashSet<JobKey>
        {
            LocationImportSchedulerKeys.Job(seed.ImportId, 3),
            LocationImportSchedulerKeys.Job(seed.ImportId, 4)
        };
        using var cancellation = new CancellationTokenSource();
        var scheduler = CancellableScheduler(keys, cancellation, removeOneFirst);

        LocationImportCommandResult result;
        await using (var command = fixture.CreateContext())
            result = await new LocationImportLifecycle(new FixtureFactory(fixture), scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance)
                .DeleteAsync(seed.UserId, seed.ImportId, cancellation.Token);

        Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        Assert.Equal(removeOneFirst ? 1 : 2, keys.Count);
        DateTime deletionAt;
        await using (var pending = fixture.CreateContext())
        {
            deletionAt = (await pending.LocationImports.FindAsync(seed.ImportId))!.DeletionRequestedAtUtc!.Value;
            Assert.Equal(DateTimeKind.Utc, deletionAt.Kind);
            Assert.InRange(deletionAt, commandStarted, DateTime.UtcNow);
            Assert.True(File.Exists(seed.Path));
        }

        var retryScheduler = StableScheduler(keys);
        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), retryScheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();
        Assert.Empty(keys);
        Assert.False(File.Exists(seed.Path));
        await reconciler.ReconcileAsync();
        retryScheduler.Verify(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()),
            Times.Exactly(removeOneFirst ? 1 : 2));
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
    }

    [PostgresFact]
    public async Task QueuedWorkerReleasedAfterProjectionDeletion_IsStaleWithoutSideEffects()
    {
        var seed = await SeedCompletedAsync(withProtectedLocation: true);
        var key = LocationImportSchedulerKeys.Job(seed.ImportId, seed.Epoch);
        var keys = new HashSet<JobKey> { key };
        var scheduler = StableScheduler(keys);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoff = new Mock<IImportEnrichmentHandoff>();
        var invocation = Task.Run(async () =>
        {
            queued.TrySetResult();
            await release.Task;
            await using var workerDb = fixture.CreateContext();
            var service = new LocationImportService(workerDb,
                new ReverseGeocodingService(new HttpClient(new RejectingHandler()),
                    NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService(), handoff.Object);
            var context = JobContext(seed.ImportId, seed.Epoch);
            await new LocationImportJob(service, NullLogger<LocationImportJob>.Instance).Execute(context.Object);
            return Assert.IsType<LocationImportExecutionOutcome>(context.Object.Result);
        });

        await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var command = fixture.CreateContext())
        {
            var result = await new LocationImportLifecycle(new FixtureFactory(fixture), scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.Accepted, result.Code);
        }
        Assert.Empty(keys);
        Assert.False(File.Exists(seed.Path));
        release.TrySetResult();
        Assert.Equal(LocationImportExecutionOutcome.Stale, await invocation);
        handoff.Verify(x => x.EnsureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        var preserved = await final.Locations.SingleAsync(x => x.Id == seed.LocationId);
        Assert.Equal("preserved-before-import", preserved.Source);
        Assert.Equal(0, await final.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == seed.UserId));
    }

    private async Task<Seed> SeedCompletedAsync(bool withProtectedLocation = false)
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-cleanup-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Latitude,Longitude\n37,23");
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = ImportStatus.Completed, ExecutionEpoch = 4, TotalRecords = 9,
            LastProcessedIndex = 4, EnrichmentRequested = true
        };
        var location = new Wayfarer.Models.Location
        {
            UserId = user.Id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC", Coordinates = new Point(23, 37) { SRID = 4326 },
            Source = "preserved-before-import"
        };
        db.Add(import);
        if (withProtectedLocation) db.Add(location);
        await db.SaveChangesAsync();
        return new(user.Id, import.Id, import.ExecutionEpoch, path, location.Id);
    }

    private static Mock<IScheduler> CancellableScheduler(HashSet<JobKey> keys,
        CancellationTokenSource cancellation, bool removeOneFirst)
    {
        var scheduler = StableScheduler(keys);
        var snapshots = 0;
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref snapshots) == 1 && !removeOneFirst) cancellation.Cancel();
                return keys.ToHashSet();
            });
        var deletes = 0;
        scheduler.Setup(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                var removed = keys.Remove(key);
                if (removeOneFirst && Interlocked.Increment(ref deletes) == 1) cancellation.Cancel();
                return removed;
            });
        return scheduler;
    }

    private static Mock<IScheduler> StableScheduler(HashSet<JobKey> keys)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => keys.ToHashSet());
        scheduler.Setup(x => x.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([]);
        scheduler.Setup(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) => keys.Remove(key));
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

    private sealed record Seed(string UserId, int ImportId, int Epoch, string Path, int LocationId);
}
