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
        scheduler.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, default))
            .Callback<IJobDetail, bool, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), default))
            .Callback<ITrigger, CancellationToken>((trigger, _) => capturedTrigger = trigger)
            .ReturnsAsync(DateTimeOffset.UtcNow);
        var workflow = LocationEnrichmentWorkflow.Create("server-user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);

        await new LocationEnrichmentScheduler(scheduler.Object).EnsureScheduledAsync(workflow);

        Assert.Equal(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId), capturedJob!.Key);
        Assert.Equal(LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch), capturedTrigger!.Key);
        Assert.Equal(workflow.SchedulerId.ToString("N"), capturedJob.JobDataMap.GetString("workflowId"));
        Assert.Equal(workflow.Epoch, capturedTrigger.JobDataMap.GetInt("epoch"));
        Assert.Equal(1, capturedJob.JobDataMap.GetInt("schema"));
        // Quartz persists milliseconds: round upward so a due trigger cannot precede relational authority.
        Assert.Equal(0, capturedTrigger.StartTimeUtc.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.True(capturedTrigger.StartTimeUtc.UtcDateTime >= workflow.NextEligibleAtUtc);
        Assert.Equal(MisfireInstruction.SimpleTrigger.FireNow,
            Assert.IsAssignableFrom<ISimpleTrigger>(capturedTrigger).MisfireInstruction);
        Assert.DoesNotContain(capturedJob.JobDataMap.Keys, key => key.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureScheduledIsIdempotentWhenJobAndTriggerExist()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        scheduler.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), default)).ReturnsAsync(true);
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync(new HashSet<TriggerKey>());
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc));

        scheduler.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), default))
            .ReturnsAsync(TriggerBuilder.Create().StartAt(workflow.NextEligibleAtUtc!.Value).Build());

        await new LocationEnrichmentScheduler(scheduler.Object).EnsureScheduledAsync(workflow);

        scheduler.Verify(item => item.AddJob(It.IsAny<IJobDetail>(), It.IsAny<bool>(), default), Times.Never);
        scheduler.Verify(item => item.ScheduleJob(It.IsAny<ITrigger>(), default), Times.Never);
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
