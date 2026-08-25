using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the bounded durable facts and command matrix presented by the import page.</summary>
public sealed class LocationEnrichmentPresentationContractTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MissingProviderCannotOfferStartOrResume()
    {
        var view = LocationEnrichmentPresentation.Build(null,
            Authority(false, "No geocoding provider is selected."), Progress(runnable: 4));

        Assert.False(view.Start.Visible);
        Assert.False(view.Resume.Visible);
        Assert.Equal("No geocoding provider is selected.", view.ProviderAvailabilitySummary);
    }

    [Theory]
    [InlineData("Provider access is not authorized.")]
    [InlineData("Provider verification is required.")]
    [InlineData("Provider verification is stale.")]
    [InlineData("The protected provider credential is unavailable.")]
    public void InvalidProviderAuthorityHasBoundedUnavailableReason(string reason)
    {
        var view = LocationEnrichmentPresentation.Build(null, Authority(false, reason), Progress(runnable: 1));

        Assert.False(view.ProviderAvailable);
        Assert.Equal(reason, view.ProviderAvailabilitySummary);
        Assert.DoesNotContain("http", view.ProviderAvailabilitySummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LocationEnrichmentState.Idle, true, false, false, false)]
    [InlineData(LocationEnrichmentState.Scheduled, false, true, false, true)]
    [InlineData(LocationEnrichmentState.Running, false, true, false, true)]
    [InlineData(LocationEnrichmentState.PausedByBudget, false, true, false, true)]
    [InlineData(LocationEnrichmentState.BackingOff, false, true, false, true)]
    [InlineData(LocationEnrichmentState.PausedByUser, false, false, true, true)]
    [InlineData(LocationEnrichmentState.PausedByAuthority, false, false, true, true)]
    [InlineData(LocationEnrichmentState.Completed, true, false, false, false)]
    [InlineData(LocationEnrichmentState.Cancelled, true, false, false, false)]
    [InlineData(LocationEnrichmentState.Failed, true, false, false, false)]
    public void CurrentAuthorityEnablesOnlyStateValidActions(LocationEnrichmentState state,
        bool start, bool pause, bool resume, bool cancel)
    {
        var workflow = Workflow(state);
        var view = LocationEnrichmentPresentation.Build(workflow, Authority(), Progress(runnable: 2));

        Assert.Equal(start, view.Start is { Visible: true, Enabled: true });
        Assert.Equal(pause, view.Pause is { Visible: true, Enabled: true });
        Assert.Equal(resume, view.Resume is { Visible: true, Enabled: true });
        Assert.Equal(cancel, view.Cancel is { Visible: true, Enabled: true });
    }

    [Fact]
    public void ProgressKeepsRunnableFutureAndPermanentWorkDistinct()
    {
        var view = LocationEnrichmentPresentation.Build(null, Authority(),
            Progress(runnable: 2, future: 3, permanent: 4));

        Assert.Equal(2, view.RunnableRemaining);
        Assert.Equal(3, view.FutureDue);
        Assert.Equal(4, view.PermanentlyDeferred);
        Assert.Equal(9, view.TotalOutstanding);
    }

    [Fact]
    public void ProviderUsageIsIndependentOfWorkflowCounters()
    {
        var workflow = Workflow(LocationEnrichmentState.Running);
        workflow.RecordBatch(12, 10, 1, 1, 99, Now);

        var view = LocationEnrichmentPresentation.Build(workflow,
            Authority(usage: 7, limit: 2500), Progress(runnable: 1));

        Assert.Equal(7, view.ProviderUsage);
        Assert.Equal(2500, view.ProviderLimit);
    }

    [Fact]
    public void ReloadedDurableFactsExposeNextAttemptAndPausedReason()
    {
        var next = Now.AddHours(2);
        var workflow = Workflow(LocationEnrichmentState.BackingOff, next);

        var view = LocationEnrichmentPresentation.Build(workflow, Authority(),
            Progress(runnable: 0, future: 1, next: next));

        Assert.Equal(next, view.NextAttemptAtUtc);
        Assert.Equal("Waiting for a bounded retry.", view.PausedReason);
    }

    [Theory]
    [InlineData(true, LocationEnrichmentState.Completed, true)]
    [InlineData(false, LocationEnrichmentState.Completed, false)]
    [InlineData(true, LocationEnrichmentState.Running, false)]
    public void RetryDeferredRequiresCurrentEligibleRowsAndRestartableState(
        bool currentEligible, LocationEnrichmentState state, bool expected)
    {
        var view = LocationEnrichmentPresentation.Build(Workflow(state), Authority(),
            Progress(permanent: 1, retryDeferred: currentEligible));

        Assert.Equal(expected, view.RetryDeferred is { Visible: true, Enabled: true });
    }

    private static LocationEnrichmentAuthorityPresentation Authority(bool available = true,
        string summary = "Provider is ready.", int usage = 0, int limit = 2500) =>
        new("geoapify", "Geoapify", available, summary, true, usage, limit,
            "credits", "rolling 24 hours", null);

    private static LocationEnrichmentProgressPresentation Progress(int runnable = 0, int future = 0,
        int permanent = 0, bool retryDeferred = false, DateTime? next = null) =>
        new(runnable, future, permanent, retryDeferred, next);

    private static LocationEnrichmentWorkflow Workflow(LocationEnrichmentState state, DateTime? next = null)
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", Now);
        workflow.Start(Now);
        switch (state)
        {
            case LocationEnrichmentState.Idle: return LocationEnrichmentWorkflow.Create("user", Now);
            case LocationEnrichmentState.Scheduled: break;
            case LocationEnrichmentState.Running: workflow.TryClaim(workflow.Epoch, Now); break;
            case LocationEnrichmentState.PausedByUser: workflow.Pause(Now); break;
            case LocationEnrichmentState.PausedByBudget:
            case LocationEnrichmentState.BackingOff:
                workflow.ContinueAs(state, state == LocationEnrichmentState.PausedByBudget
                    ? LocationEnrichmentOutcome.BudgetExhausted : LocationEnrichmentOutcome.RetryableFailure,
                    next ?? Now.AddHours(1), Now); break;
            case LocationEnrichmentState.PausedByAuthority:
                workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, Now); break;
            case LocationEnrichmentState.Completed:
            case LocationEnrichmentState.Cancelled:
            case LocationEnrichmentState.Failed:
                workflow.TransitionToTerminal(state, state == LocationEnrichmentState.Failed
                    ? LocationEnrichmentOutcome.DataFailure : LocationEnrichmentOutcome.NoCandidates, Now); break;
        }
        return workflow;
    }
}
