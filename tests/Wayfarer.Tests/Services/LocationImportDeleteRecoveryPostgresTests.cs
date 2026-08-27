using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves deletion intent survives external failures and worker terminal races.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportDeleteRecoveryPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task ReconcilerSchedulerFailure_RetainsFileRowAndIntentUntilRetry()
    {
        var seed = await SeedAsync(ImportStatus.Completed, epoch: 4);
        var key = LocationImportSchedulerKeys.Job(seed.ImportId, 4);
        var scheduler = Scheduler(key);
        scheduler.Setup(item => item.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(item => item.DeleteJob(key, default))
            .ThrowsAsync(new SchedulerException("fixture scheduler unavailable"));

        await using (var command = fixture.CreateContext())
        {
            var result = await Lifecycle(command, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        }

        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();

        await using (var pending = fixture.CreateContext())
        {
            Assert.NotNull((await pending.LocationImports.FindAsync(seed.ImportId))!.DeletionRequestedAtUtc);
            Assert.True(File.Exists(seed.Path));
        }

        scheduler.Setup(item => item.DeleteJob(key, default)).ReturnsAsync(true);
        scheduler.Setup(item => item.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(item => item.GetJobKeys(
            It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default)).ReturnsAsync([]);
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();

        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.False(File.Exists(seed.Path));
    }

    [PostgresFact]
    public async Task NewerProjectionAppearingDuringCleanup_RetainsFileRowAndIntent()
    {
        var seed = await SeedAsync(ImportStatus.Completed, epoch: 4);
        var oldKey = LocationImportSchedulerKeys.Job(seed.ImportId, 4);
        var newKey = LocationImportSchedulerKeys.Job(seed.ImportId, 5);
        var calls = 0;
        var scheduler = Scheduler();
        scheduler.Setup(item => item.GetJobKeys(
                It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1 ? [oldKey] : [newKey]);
        scheduler.Setup(item => item.DeleteJob(oldKey, default)).ReturnsAsync(true);

        await using (var command = fixture.CreateContext())
        {
            var result = await Lifecycle(command, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        }

        await using var verification = fixture.CreateContext();
        Assert.NotNull((await verification.LocationImports.FindAsync(seed.ImportId))!.DeletionRequestedAtUtc);
        Assert.True(File.Exists(seed.Path));
        File.Delete(seed.Path);
    }

    [PostgresFact]
    public async Task LockedUpload_RetainsIntentThenReconciliationDeletesIdempotently()
    {
        var seed = await SeedAsync(ImportStatus.Completed);
        await using var lockStream = new FileStream(seed.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scheduler = Scheduler();
        await using (var command = fixture.CreateContext())
        {
            var result = await Lifecycle(command, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        }
        await using (var verification = fixture.CreateContext())
            Assert.NotNull((await verification.LocationImports.FindAsync(seed.ImportId))!.DeletionRequestedAtUtc);

        await lockStream.DisposeAsync();
        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();

        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.False(File.Exists(seed.Path));
        Assert.NotNull(await final.Users.FindAsync(seed.UserId));
    }

    [PostgresTheory]
    [InlineData(LocationImportExecutionOutcome.Completed)]
    [InlineData(LocationImportExecutionOutcome.Failed)]
    public async Task DeleteIntentFirst_FencesWorkerTerminalWriteUntilCleanup(
        LocationImportExecutionOutcome outcome)
    {
        var seed = await SeedAsync(ImportStatus.Completed, epoch: 3);
        var scheduler = Scheduler(LocationImportSchedulerKeys.Job(seed.ImportId, 3));
        await using (var command = fixture.CreateContext())
        {
            var result = await Lifecycle(command, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        }
        await using (var worker = fixture.CreateContext())
            await Lifecycle(worker, scheduler.Object).ConvergeExecutionAsync(seed.ImportId, 3, outcome);

        await using (var pending = fixture.CreateContext())
        {
            var current = await pending.LocationImports.FindAsync(seed.ImportId);
            Assert.NotNull(current!.DeletionRequestedAtUtc);
            Assert.Equal(ImportStatus.Completed, current.Status);
            Assert.Null(current.ErrorMessage);
            Assert.True(File.Exists(seed.Path));
        }

        scheduler = Scheduler();
        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await reconciler.ReconcileAsync();
        await reconciler.ReconcileAsync();
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.False(File.Exists(seed.Path));
    }

    [PostgresTheory]
    [InlineData(LocationImportExecutionOutcome.Completed, "Completed")]
    [InlineData(LocationImportExecutionOutcome.Failed, "Failed")]
    public async Task WorkerTerminalWriteFirst_RemainsTruthfulUntilMetadataDeletion(
        LocationImportExecutionOutcome outcome, string expectedStatus)
    {
        var seed = await SeedAsync(ImportStatus.InProgress, epoch: 7);
        var scheduler = Scheduler();
        await using (var worker = fixture.CreateContext())
            await Lifecycle(worker, scheduler.Object).ConvergeExecutionAsync(seed.ImportId, 7, outcome);
        await using (var terminal = fixture.CreateContext())
        {
            var current = await terminal.LocationImports.FindAsync(seed.ImportId);
            Assert.Equal(expectedStatus, current!.Status.Value);
            Assert.Equal(7, current.ExecutionEpoch);
            Assert.Equal(4, current.LastProcessedIndex);
            Assert.Equal(9, current.TotalRecords);
            Assert.Equal(outcome == LocationImportExecutionOutcome.Failed ? "Import processing failed." : null,
                current.ErrorMessage);
        }
        await using (var command = fixture.CreateContext())
            Assert.Equal(LocationImportCommandCode.Accepted,
                (await Lifecycle(command, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId)).Code);
        await using var final = fixture.CreateContext();
        Assert.Null(await final.LocationImports.FindAsync(seed.ImportId));
        Assert.False(File.Exists(seed.Path));
    }

    private async Task<(string UserId, int ImportId, string Path)> SeedAsync(ImportStatus status, int epoch = 0)
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "fixture");
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = status, ExecutionEpoch = epoch, TotalRecords = 9, LastProcessedIndex = 4
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return (user.Id, import.Id, path);
    }

    private LocationImportLifecycle Lifecycle(ApplicationDbContext db, IScheduler scheduler) =>
        new(new FixtureFactory(fixture), scheduler, NullLogger<LocationImportLifecycle>.Instance);

    private static Mock<IScheduler> Scheduler(JobKey? executing = null)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync(executing is null ? [] : [executing]);
        scheduler.Setup(item => item.DeleteJob(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        var executions = new List<IJobExecutionContext>();
        if (executing is not null)
        {
            var context = new Mock<IJobExecutionContext>();
            var detail = new Mock<IJobDetail>();
            detail.SetupGet(item => item.Key).Returns(executing);
            context.SetupGet(item => item.JobDetail).Returns(detail.Object);
            executions.Add(context.Object);
        }
        scheduler.Setup(item => item.GetCurrentlyExecutingJobs(default)).ReturnsAsync(executions);
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync([]);
        return scheduler;
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
