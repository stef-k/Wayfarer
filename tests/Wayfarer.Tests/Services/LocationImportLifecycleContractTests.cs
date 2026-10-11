using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NetTopologySuite.Geometries;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the durable convergence contract for location-import commands and recovery.</summary>
public sealed class LocationImportLifecycleContractTests : TestBase
{
    [Fact]
    public void Lifecycle_DependsOnContextFactory_NotScopedContext()
    {
        var constructor = Assert.Single(typeof(LocationImportLifecycle).GetConstructors());

        Assert.Contains(constructor.GetParameters(), parameter =>
            parameter.ParameterType == typeof(IDbContextFactory<ApplicationDbContext>));
        Assert.DoesNotContain(constructor.GetParameters(), parameter =>
            parameter.ParameterType == typeof(ApplicationDbContext));
    }

    [Fact]
    public async Task Start_CommitsIntent_WhenSchedulingThrows()
    {
        await using var db = CreateDbContext();
        db.LocationImports.Add(NewImport());
        await db.SaveChangesAsync();
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default))
            .ThrowsAsync(new SchedulerException("unavailable"));

        var result = await Owner(db, scheduler.Object).StartAsync("owner", 1);

        db.ChangeTracker.Clear();
        Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        Assert.Equal(ImportStatus.InProgress, db.LocationImports.Single().Status);
        Assert.True(db.LocationImports.Single().ProjectionPending);
    }

    [Fact]
    public async Task ConcurrentStarts_ReuseOneEpochAndProjection()
    {
        await using var db = CreateDbContext();
        db.LocationImports.Add(NewImport());
        await db.SaveChangesAsync();
        var scheduler = Scheduler();
        var owner = Owner(db, scheduler.Object);

        await Task.WhenAll(owner.StartAsync("owner", 1), owner.StartAsync("owner", 1));

        db.ChangeTracker.Clear();
        Assert.Equal(1, db.LocationImports.Single().ExecutionEpoch);
        scheduler.Verify(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Once);
    }

    [Fact]
    public async Task StartRacingStop_LeavesDurableStopIntent()
    {
        await AssertStopIntentAsync(interruptResult: false, jobExists: true);
    }

    [Fact]
    public async Task CooperativeStop_RemainsStoppingUntilWorkerAcknowledges()
    {
        await AssertStopIntentAsync(interruptResult: true, jobExists: true);
    }

    [Fact]
    public async Task InterruptFalse_RemainsStopping()
    {
        await AssertStopIntentAsync(interruptResult: false, jobExists: true);
    }

    [Fact]
    public async Task MissingJobStop_RemainsRecoverable()
    {
        await AssertStopIntentAsync(interruptResult: false, jobExists: false);
    }

    [Theory]
    [InlineData(LocationImportExecutionOutcome.Completed)]
    [InlineData(LocationImportExecutionOutcome.Failed)]
    [InlineData(LocationImportExecutionOutcome.StagedFileUnavailable)]
    public async Task StopWinsTerminalWorkerRace(LocationImportExecutionOutcome outcome)
    {
        await using var db = CreateDbContext();
        db.LocationImports.Add(NewImport(ImportStatus.Stopping, epoch: 4));
        await db.SaveChangesAsync();

        await Owner(db, Scheduler().Object).ConvergeExecutionAsync(1, 4, outcome);

        db.ChangeTracker.Clear();
        Assert.Equal(ImportStatus.Stopped, db.LocationImports.Single().Status);
    }

    [Fact]
    public async Task RestartRepairsActiveImportWithMissingProjection()
    {
        await AssertReconciliationAsync(NewImport(ImportStatus.InProgress, epoch: 2), ImportStatus.InProgress);
    }

    /// <summary>A failed or silently ineffective Quartz repair retains durable retry intent until verified.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnverifiedTriggerRepair_RemainsPendingAndRetries(bool throws)
    {
        var contexts = new InMemoryFactory();
        await using (var db = contexts.CreateDbContext())
        {
            db.LocationImports.Add(NewImport(ImportStatus.InProgress, epoch: 3));
            await db.SaveChangesAsync();
        }
        var scheduler = Scheduler(jobExists: true, projectedEpoch: 3);
        var trigger = LocationImportSchedulerKeys.BuildTrigger(1, 3).GetTriggerBuilder()
            .ForJob(new JobKey("OtherJob", "Imports")).Build();
        scheduler.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), default)).ReturnsAsync(() => trigger);
        var replacement = scheduler.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(),
            It.IsAny<IReadOnlyCollection<ITrigger>>(), true, default));
        if (throws) replacement.ThrowsAsync(new SchedulerException("fixture replacement failure"));
        else replacement.Returns(Task.CompletedTask);

        await Reconciler(contexts, scheduler.Object).ReconcileAsync();
        await Reconciler(contexts, scheduler.Object).ReconcileAsync();
        await using (var pending = contexts.CreateDbContext())
            Assert.True((await pending.LocationImports.FindAsync(1))!.ProjectionPending);
        Assert.Equal(new JobKey("OtherJob", "Imports"), trigger.JobKey);
        scheduler.Verify(item => item.DeleteJob(It.IsAny<JobKey>(), default), Times.Never);

        replacement.Callback<IJobDetail, IReadOnlyCollection<ITrigger>, bool, CancellationToken>(
            (_, replacements, _, _) => trigger = Assert.Single(replacements)).Returns(Task.CompletedTask);
        await Reconciler(contexts, scheduler.Object).ReconcileAsync();
        await using var repaired = contexts.CreateDbContext();
        Assert.False((await repaired.LocationImports.FindAsync(1))!.ProjectionPending);
        Assert.Equal(LocationImportSchedulerKeys.Job(1, 3), trigger.JobKey);
    }

    [Fact]
    public async Task RestartFinalizesStoppingImportWithoutExecution()
    {
        await AssertReconciliationAsync(NewImport(ImportStatus.Stopping, epoch: 2), ImportStatus.Stopped);
    }

    [Fact]
    public async Task RestartReplacesStaleProjection()
    {
        var contexts = new InMemoryFactory();
        await using var db = contexts.CreateDbContext();
        db.LocationImports.Add(NewImport(ImportStatus.InProgress, epoch: 3));
        await db.SaveChangesAsync();
        var scheduler = Scheduler(jobExists: true, projectedEpoch: 2);

        await Reconciler(contexts, scheduler.Object).ReconcileAsync();

        scheduler.Verify(item => item.DeleteJob(LocationImportSchedulerKeys.Job(1, 2), default), Times.Once);
        scheduler.Verify(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Once);
    }

    [Fact]
    public async Task DeleteRejectsExecutingOrStoppingImportWithoutDeletingFile()
    {
        await using var db = CreateDbContext();
        var import = NewImport(ImportStatus.Stopping, epoch: 1);
        import.FilePath = ImportStaging.TempFile();
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var result = await Owner(db, Scheduler().Object).DeleteAsync("owner", 1);

        Assert.Equal(LocationImportCommandCode.ExecutionActive, result.Code);
        Assert.True(File.Exists(import.FilePath));
        File.Delete(import.FilePath);
    }

    [Theory]
    [InlineData(LocationImportExecutionOutcome.Cancelled, "Cancelled")]
    [InlineData(LocationImportExecutionOutcome.Stale, "Cancelled")]
    [InlineData(LocationImportExecutionOutcome.Completed, "Completed")]
    [InlineData(LocationImportExecutionOutcome.Failed, "Failed")]
    [InlineData(LocationImportExecutionOutcome.StagedFileUnavailable, "Failed")]
    public void JobOutcome_MapsToTruthfulHistory(LocationImportExecutionOutcome outcome, string expected)
        => Assert.Equal(expected, LocationImportJobOutcome.ToHistoryStatus(outcome));

    [Fact]
    public async Task TerminalConvergencePreservesLocationsAndEnrichmentAdmission()
    {
        await using var db = CreateDbContext();
        var import = NewImport(ImportStatus.Stopping, epoch: 1);
        import.RemainingEnrichmentCount = 1;
        db.LocationImports.Add(import);
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = "owner", Timestamp = DateTime.UtcNow, TimeZoneId = "UTC",
            Coordinates = new Point(1, 1) { SRID = 4326 }
        });
        await db.SaveChangesAsync();

        await Owner(db, Scheduler().Object).ConvergeExecutionAsync(1, 1, LocationImportExecutionOutcome.Cancelled);

        Assert.Single(db.Locations);
        Assert.Equal(1, db.LocationImports.Single().RemainingEnrichmentCount);
    }

    private async Task AssertStopIntentAsync(bool interruptResult, bool jobExists)
    {
        await using var db = CreateDbContext();
        db.LocationImports.Add(NewImport(ImportStatus.InProgress, epoch: 1));
        await db.SaveChangesAsync();
        var scheduler = Scheduler(jobExists);
        scheduler.Setup(item => item.Interrupt(It.IsAny<JobKey>(), default)).ReturnsAsync(interruptResult);

        await Owner(db, scheduler.Object).StopAsync("owner", 1);

        db.ChangeTracker.Clear();
        Assert.Equal(ImportStatus.Stopping, db.LocationImports.Single().Status);
        Assert.True(db.LocationImports.Single().StopRequestedAtUtc.HasValue);
    }

    private async Task AssertReconciliationAsync(LocationImport import, ImportStatus expected)
    {
        var contexts = new InMemoryFactory();
        await using (var db = contexts.CreateDbContext())
        { db.LocationImports.Add(import); await db.SaveChangesAsync(); }
        await Reconciler(contexts, Scheduler().Object).ReconcileAsync();
        await using var verification = contexts.CreateDbContext();
        Assert.Equal(expected, verification.LocationImports.Single().Status);
    }

    private static LocationImportLifecycle Owner(ApplicationDbContext db, IScheduler scheduler)
        => new(ImportStaging.Files, new CloningFactory(db), scheduler, NullLogger<LocationImportLifecycle>.Instance);

    private static LocationImportReconciler Reconciler(
        IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler)
        => new(ImportStaging.Files, contexts, scheduler, NullLogger<LocationImportReconciler>.Instance);

    private sealed class InMemoryFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options =
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }

    private sealed class CloningFactory(ApplicationDbContext source) : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options =
            Assert.IsType<DbContextOptions<ApplicationDbContext>>(source.GetService<IDbContextOptions>());
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }

    /// <summary>Retains job payloads and trigger links so verification observes the fixture's actual projections.</summary>
    private static Mock<IScheduler> Scheduler(bool jobExists = false, int? projectedEpoch = null)
    {
        var scheduler = new Mock<IScheduler>();
        var jobs = new Dictionary<JobKey, IJobDetail>();
        var triggers = new Dictionary<TriggerKey, ITrigger>();
        if (jobExists && projectedEpoch is not null)
        {
            var job = LocationImportSchedulerKeys.BuildJob(1, projectedEpoch.Value);
            jobs.Add(job.Key, job);
            var trigger = LocationImportSchedulerKeys.BuildTrigger(1, projectedEpoch.Value);
            triggers.Add(trigger.Key, trigger);
        }
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobExists && projectedEpoch is null || jobs.ContainsKey(key));
        scheduler.Setup(item => item.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync(() => jobs.Keys.ToHashSet());
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(() => triggers.Keys.ToHashSet());
        scheduler.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), default))
            .ReturnsAsync((TriggerKey key, CancellationToken _) => triggers.ContainsKey(key));
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), default))
            .Callback<ITrigger, CancellationToken>((trigger, _) => triggers[trigger.Key] = trigger)
            .ReturnsAsync(DateTimeOffset.UtcNow);
        scheduler.Setup(item => item.GetJobDetail(It.IsAny<JobKey>(), default))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobs.GetValueOrDefault(key));
        scheduler.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), default))
            .ReturnsAsync((TriggerKey key, CancellationToken _) => triggers.GetValueOrDefault(key));
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default))
            .Callback<IJobDetail, ITrigger, CancellationToken>((job, trigger, _) =>
            {
                jobs[job.Key] = job;
                triggers[trigger.Key] = trigger;
            })
            .ReturnsAsync(DateTimeOffset.UtcNow);
        scheduler.Setup(item => item.DeleteJob(It.IsAny<JobKey>(), default))
            .ReturnsAsync((JobKey key, CancellationToken _) =>
            {
                foreach (var trigger in triggers.Values.Where(trigger => trigger.JobKey.Equals(key)).ToList())
                    triggers.Remove(trigger.Key);
                return jobs.Remove(key);
            });
        return scheduler;
    }

    private static LocationImport NewImport(ImportStatus? status = null, int epoch = 0) => new()
    {
        Id = 1,
        UserId = "owner",
        FilePath = LocationImportStagedFiles.CreateReference(LocationImportFileType.Csv),
        FileType = LocationImportFileType.Csv,
        TotalRecords = 0,
        LastProcessedIndex = 0,
        Status = status ?? ImportStatus.Stopped,
        ExecutionEpoch = epoch
    };
}
