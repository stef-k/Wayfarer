using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Guards bounded, snapshot-once reconciliation without provider contact.</summary>
public sealed class LocationEnrichmentReconcilerTests
{
    [Fact]
    public async Task PauseCommittedAfterPageLoadCannotLeaveScheduledTrigger()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        await using var db = new ApplicationDbContext(options, services);
        var workflow = LocationEnrichmentWorkflow.Create("race-user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        await db.SaveChangesAsync();
        var triggerKey = LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch);
        var liveTriggers = new HashSet<TriggerKey>();
        var quartz = SchedulerWithExistingProjection([]);
        quartz.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        quartz.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Returns<ITrigger, CancellationToken>(async (trigger, _) =>
            {
                await using var command = new ApplicationDbContext(options, services);
                var current = await command.LocationEnrichmentWorkflows.SingleAsync();
                current.Pause(DateTime.UtcNow);
                await command.SaveChangesAsync();
                liveTriggers.Add(trigger.Key);
                return DateTimeOffset.UtcNow;
            });
        quartz.Setup(item => item.UnscheduleJob(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .Returns<TriggerKey, CancellationToken>((key, _) => Task.FromResult(liveTriggers.Remove(key)));

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        Assert.DoesNotContain(triggerKey, liveTriggers);
    }

    [Fact]
    public async Task WorkflowCreatedDuringOrphanDeletionIsReprojected()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        await using var db = new ApplicationDbContext(options, services);
        var schedulerId = Guid.NewGuid();
        var orphan = LocationEnrichmentScheduler.JobKey(schedulerId);
        var quartz = SchedulerWithExistingProjection([orphan]);
        quartz.Setup(item => item.DeleteJob(orphan, It.IsAny<CancellationToken>()))
            .Returns<JobKey, CancellationToken>(async (_, _) =>
            {
                await using var command = new ApplicationDbContext(options, services);
                var workflow = LocationEnrichmentWorkflow.Create("created-during-delete", DateTime.UtcNow);
                typeof(LocationEnrichmentWorkflow).GetProperty(nameof(LocationEnrichmentWorkflow.SchedulerId))!
                    .SetValue(workflow, schedulerId);
                workflow.Start(DateTime.UtcNow);
                command.Add(workflow);
                await command.SaveChangesAsync();
                return true;
            });

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        quartz.Verify(item => item.AddJob(It.Is<IJobDetail>(job => job.Key.Name == orphan.Name
            && job.Key.Group == orphan.Group), false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TriggerSnapshotIsNotEnumeratedPerWorkflow()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var triggerItems = new List<TriggerKey>();
        for (var index = 0; index < 1_001; index++)
        {
            var workflow = LocationEnrichmentWorkflow.Create($"linear-{index:D4}", DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            db.Add(workflow);
            triggerItems.Add(LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch));
        }
        await db.SaveChangesAsync();
        var triggers = new CountingCollection<TriggerKey>(triggerItems);
        var quartz = new Mock<IScheduler>();
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(triggers);
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<JobKey>());
        quartz.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        Assert.Equal(1, triggers.EnumerationCount);
        quartz.Verify(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ReconcilerOwnsPageScopedContextFactory()
    {
        var parameter = Assert.Single(typeof(LocationEnrichmentReconciler).GetConstructors()).GetParameters()[0];
        Assert.Equal(typeof(IDbContextFactory<ApplicationDbContext>), parameter.ParameterType);
    }
    [Fact]
    public async Task WorkflowBeyondFirstThousandIsNotDeletedAsAnOrphan()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        LocationEnrichmentWorkflow? last = null;
        for (var index = 0; index < 1_001; index++)
        {
            last = LocationEnrichmentWorkflow.Create($"user-{index:D4}", DateTime.UtcNow);
            last.Start(DateTime.UtcNow);
            db.Add(last);
        }
        await db.SaveChangesAsync();
        var protectedJob = LocationEnrichmentScheduler.JobKey(last!.SchedulerId);
        var quartz = SchedulerWithExistingProjection([protectedJob]);

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        quartz.Verify(item => item.DeleteJob(protectedJob, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApparentOrphanIsVerifiedAgainstRelationalAuthorityBeforeDeletion()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        LocationEnrichmentWorkflow? authoritative = null;
        for (var index = 0; index < 1_001; index++)
        {
            authoritative = LocationEnrichmentWorkflow.Create($"user-{index:D4}", DateTime.UtcNow);
            authoritative.Start(DateTime.UtcNow);
            db.Add(authoritative);
        }
        await db.SaveChangesAsync();
        var job = LocationEnrichmentScheduler.JobKey(authoritative!.SchedulerId);
        var quartz = SchedulerWithExistingProjection([job]);

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        quartz.Verify(item => item.DeleteJob(job, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationBetweenPagesStopsBeforeLaterSchedulerMutations()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        for (var index = 0; index < 401; index++)
        {
            var workflow = LocationEnrichmentWorkflow.Create($"user-{index:D4}", DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            db.Add(workflow);
        }
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var additions = 0;
        var quartz = new Mock<IScheduler>();
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<TriggerKey>());
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<JobKey>());
        quartz.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .Callback(() => { if (Interlocked.Increment(ref additions) == 200) cancellation.Cancel(); })
            .Returns(Task.CompletedTask);
        quartz.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
                .ReconcileAsync(cancellation.Token));

        Assert.Equal(200, additions);
    }

    [Fact]
    public async Task SchedulerGroupsAreSnapshottedOnceForManyWorkflows()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        for (var index = 0; index < 50; index++)
        {
            var workflow = LocationEnrichmentWorkflow.Create($"user-{index:D3}", DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            db.Add(workflow);
        }
        await db.SaveChangesAsync();
        var quartz = new Mock<IScheduler>();
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(new HashSet<TriggerKey>());
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), default))
            .ReturnsAsync(new HashSet<JobKey>());
        quartz.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        quartz.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), default)).ReturnsAsync(true);

        await new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        quartz.Verify(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), default), Times.Once);
        quartz.Verify(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), default), Times.Once);
        quartz.Verify(item => item.CheckExists(It.IsAny<JobKey>(), default), Times.Never);
        quartz.Verify(item => item.CheckExists(It.IsAny<TriggerKey>(), default), Times.Never);
        quartz.Verify(item => item.AddJob(It.IsAny<IJobDetail>(), false, default), Times.Exactly(50));
    }

    [Fact]
    public async Task CancellationBeforePagePerformsNoSchedulerMutation()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var quartz = new Mock<IScheduler>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocationEnrichmentReconciler(new TestContextFactory(options, services), new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
                .ReconcileAsync(cancellation.Token));

        quartz.Verify(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IScheduler> SchedulerWithExistingProjection(IReadOnlyCollection<JobKey> jobs)
    {
        var quartz = new Mock<IScheduler>();
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<TriggerKey>());
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs.ToHashSet());
        quartz.Setup(item => item.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        quartz.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return quartz;
    }

    private sealed class CountingCollection<T>(IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
    {
        public int EnumerationCount { get; private set; }
        public int Count => items.Count;

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestContextFactory(
        DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }
}
