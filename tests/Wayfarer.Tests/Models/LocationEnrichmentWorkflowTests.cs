using Wayfarer.Models.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Defines the durable enrichment workflow transition contract.</summary>
public sealed class LocationEnrichmentWorkflowTests
{
    [Fact]
    public void ExecutionLeaseIsFencedAndStaleOwnersCannotMutateOrRelease()
    {
        var now = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
        var workflow = LocationEnrichmentWorkflow.Create("user", now);
        workflow.Start(now);

        var first = workflow.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(20));
        Assert.NotNull(first);
        Assert.Null(workflow.TryAcquireExecutionLease(now.AddSeconds(1), TimeSpan.FromSeconds(20)));
        Assert.True(workflow.TryRenewExecutionLease(first.Value.LeaseId, first.Value.FencingGeneration,
            now.AddSeconds(2), TimeSpan.FromSeconds(20)));
        Assert.False(workflow.TryReleaseExecutionLease(Guid.NewGuid(), first.Value.FencingGeneration));
        Assert.True(workflow.TryReleaseExecutionLease(first.Value.LeaseId, first.Value.FencingGeneration));

        var second = workflow.TryAcquireExecutionLease(now.AddSeconds(3), TimeSpan.FromSeconds(20));
        Assert.NotNull(second);
        Assert.True(second.Value.FencingGeneration > first.Value.FencingGeneration);
        Assert.False(workflow.HasExecutionLease(first.Value.LeaseId, first.Value.FencingGeneration,
            now.AddSeconds(4)));
    }

    [Fact]
    public void PauseAndCancelFenceAnActiveExecutionLease()
    {
        var now = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
        var paused = LocationEnrichmentWorkflow.Create("paused", now);
        paused.Start(now);
        var pauseLease = paused.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(20))!.Value;
        paused.Pause(now.AddSeconds(1));
        Assert.False(paused.HasExecutionLease(pauseLease.LeaseId, pauseLease.FencingGeneration, now.AddSeconds(2)));

        var cancelled = LocationEnrichmentWorkflow.Create("cancelled", now);
        cancelled.Start(now);
        var cancelLease = cancelled.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(20))!.Value;
        cancelled.Cancel(now.AddSeconds(1));
        Assert.False(cancelled.HasExecutionLease(cancelLease.LeaseId, cancelLease.FencingGeneration, now.AddSeconds(2)));
    }
    [Fact]
    public void StartAndResumeAreIdempotentWithinAnActiveEpoch()
    {
        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var workflow = LocationEnrichmentWorkflow.Create("user", now);

        workflow.Start(now);
        workflow.Start(now.AddSeconds(1));
        var epoch = workflow.Epoch;
        workflow.Pause(now.AddSeconds(2));
        workflow.Resume(now.AddSeconds(3));
        workflow.Resume(now.AddSeconds(4));

        Assert.Equal(1, epoch);
        Assert.Equal(epoch + 1, workflow.Epoch);
        Assert.Equal(LocationEnrichmentState.Scheduled, workflow.State);
        Assert.True(workflow.IntentEnabled);
    }

    [Theory]
    [InlineData(LocationEnrichmentState.Completed)]
    [InlineData(LocationEnrichmentState.Cancelled)]
    [InlineData(LocationEnrichmentState.Failed)]
    public void StartAfterTerminalStateAdvancesEpochWithoutResettingCounters(LocationEnrichmentState terminal)
    {
        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var workflow = LocationEnrichmentWorkflow.Create("user", now);
        workflow.Start(now);
        workflow.RecordBatch(10, 7, 1, 2, 3, now.AddMinutes(1));
        workflow.TransitionToTerminal(terminal, LocationEnrichmentOutcome.None, now.AddMinutes(2));

        workflow.Start(now.AddMinutes(3));

        Assert.Equal(2, workflow.Epoch);
        Assert.Equal(10, workflow.ProcessedCount);
        Assert.Equal(7, workflow.EnrichedCount);
        Assert.Equal(3, workflow.AdmittedUsageCount);
    }

    [Fact]
    public void PauseAndCancelDisableContactIntentBeforeInterruption()
    {
        var now = DateTime.UtcNow;
        var workflow = LocationEnrichmentWorkflow.Create("user", now);
        workflow.Start(now);

        workflow.Pause(now.AddSeconds(1));
        Assert.False(workflow.IntentEnabled);
        Assert.Equal(LocationEnrichmentState.PausedByUser, workflow.State);

        workflow.Resume(now.AddSeconds(2));
        workflow.Cancel(now.AddSeconds(3));
        Assert.False(workflow.IntentEnabled);
        Assert.Equal(LocationEnrichmentState.Cancelled, workflow.State);
    }
    /// <summary>Automatic waiting states acquire only at their durable deadline and retain their epoch.</summary>
    [Theory]
    [InlineData(LocationEnrichmentState.BackingOff)]
    [InlineData(LocationEnrichmentState.PausedByBudget)]
    public void AutomaticContinuationCannotAcquireBeforeDue(LocationEnrichmentState state)
    {
        var now = DateTime.UtcNow;
        var workflow = LocationEnrichmentWorkflow.Create("due", now);
        workflow.Start(now);
        var epoch = workflow.Epoch;
        workflow.ContinueAs(state, LocationEnrichmentOutcome.RetryableFailure, now.AddMinutes(5), now);
        Assert.Null(workflow.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(35)));
        Assert.Equal(state, workflow.State);
        var lease = workflow.TryAcquireExecutionLease(now.AddMinutes(5), TimeSpan.FromSeconds(35));
        Assert.NotNull(lease);
        Assert.Equal(epoch, lease.Value.Epoch);
        Assert.Equal(LocationEnrichmentState.Running, workflow.State);
    }
}
