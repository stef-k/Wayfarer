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
}
