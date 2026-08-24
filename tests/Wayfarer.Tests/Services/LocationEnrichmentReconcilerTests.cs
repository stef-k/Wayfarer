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

        await new LocationEnrichmentReconciler(db, new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
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

        await new LocationEnrichmentReconciler(db, new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
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
        var checks = 0;
        var quartz = new Mock<IScheduler>();
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<TriggerKey>());
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<JobKey>());
        quartz.Setup(item => item.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (Interlocked.Increment(ref checks) == 200) cancellation.Cancel(); return true; });
        quartz.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocationEnrichmentReconciler(db, new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
                .ReconcileAsync(cancellation.Token));

        Assert.Equal(200, checks);
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

        await new LocationEnrichmentReconciler(db, new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
            .ReconcileAsync();

        quartz.Verify(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), default), Times.Once);
        quartz.Verify(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), default), Times.Once);
        quartz.Verify(item => item.CheckExists(It.IsAny<TriggerKey>(), default), Times.Exactly(50));
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
            new LocationEnrichmentReconciler(db, new LocationEnrichmentScheduler(quartz.Object), quartz.Object)
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
}
