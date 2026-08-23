using Moq;
using Quartz;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines stable Quartz identity and stale-epoch scheduling behavior.</summary>
public sealed class LocationEnrichmentSchedulerTests
{
    [Fact]
    public async Task EnsureScheduledUsesStableServerKeysAndOneShotDoNothingMisfire()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(false);
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(new HashSet<TriggerKey>());
        IJobDetail? capturedJob = null;
        ITrigger? capturedTrigger = null;
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default))
            .Callback<IJobDetail, ITrigger, CancellationToken>((job, trigger, _) => { capturedJob = job; capturedTrigger = trigger; })
            .ReturnsAsync(DateTimeOffset.UtcNow);
        var workflow = LocationEnrichmentWorkflow.Create("server-user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);

        await new LocationEnrichmentScheduler(scheduler.Object).EnsureScheduledAsync(workflow);

        Assert.Equal(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId), capturedJob!.Key);
        Assert.Equal(LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch), capturedTrigger!.Key);
        Assert.Equal(workflow.SchedulerId.ToString("N"), capturedJob.JobDataMap.GetString("workflowId"));
        Assert.Equal(workflow.Epoch, capturedTrigger.JobDataMap.GetInt("epoch"));
        Assert.Equal(1, capturedJob.JobDataMap.GetInt("schema"));
        Assert.Equal(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount,
            Assert.IsAssignableFrom<ISimpleTrigger>(capturedTrigger).MisfireInstruction);
        Assert.DoesNotContain(capturedJob.JobDataMap.Keys, key => key.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureScheduledIsIdempotentWhenJobAndTriggerExist()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(new HashSet<TriggerKey>());
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);

        await new LocationEnrichmentScheduler(scheduler.Object).EnsureScheduledAsync(workflow);

        scheduler.Verify(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Never);
    }

    [Fact]
    public async Task ExistingStaleEpochTriggerIsReplacedByCurrentOneShotTrigger()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        scheduler.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), default)).ReturnsAsync(false);
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(new HashSet<TriggerKey>());
        ITrigger? scheduled = null;
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<ITrigger, CancellationToken>((trigger, _) => scheduled = trigger)
            .ReturnsAsync(DateTimeOffset.UtcNow);
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);

        await new LocationEnrichmentScheduler(scheduler.Object).EnsureScheduledAsync(workflow);

        Assert.Equal(LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch), scheduled?.Key);
    }
}
