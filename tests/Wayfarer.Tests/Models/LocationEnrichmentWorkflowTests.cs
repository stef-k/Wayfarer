using Wayfarer.Models.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Defines the durable enrichment workflow transition contract.</summary>
public sealed class LocationEnrichmentWorkflowTests
{
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
        Assert.Equal(epoch, workflow.Epoch);
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
}
