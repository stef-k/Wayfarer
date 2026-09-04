using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Maps bounded durable facts to the complete user command presentation.</summary>
public static class LocationEnrichmentPresentation
{
    public static LocationEnrichmentPresentationModel Build(LocationEnrichmentWorkflow? workflow,
        LocationEnrichmentAuthorityPresentation authority, LocationEnrichmentProgressPresentation progress)
    {
        var state = workflow?.State ?? LocationEnrichmentState.Idle;
        var active = state is LocationEnrichmentState.Scheduled or LocationEnrichmentState.Running
            or LocationEnrichmentState.PausedByBudget or LocationEnrichmentState.BackingOff;
        var resumable = state is LocationEnrichmentState.PausedByUser or LocationEnrichmentState.PausedByAuthority;
        var restartable = state is LocationEnrichmentState.Idle or LocationEnrichmentState.Completed
            or LocationEnrichmentState.Cancelled or LocationEnrichmentState.Failed;
        var hasRunnableWork = progress.RunnableRemaining > 0;
        var canStart = restartable && authority.Available && hasRunnableWork;
        var canResume = resumable && authority.Available;
        var canRetry = workflow?.State is (LocationEnrichmentState.PausedByAuthority
            or LocationEnrichmentState.Completed or LocationEnrichmentState.Failed)
            && authority.Available && progress.ManualRetryAvailable > 0;
        var canRepair = workflow?.State is (LocationEnrichmentState.PausedByAuthority
            or LocationEnrichmentState.Completed or LocationEnrichmentState.Failed)
            && authority.Available && authority.ProviderKey == "geoapify"
            && progress.IncompleteProviderAddresses > 0;
        var pausedReason = state switch
        {
            LocationEnrichmentState.PausedByUser => "Paused by you.",
            LocationEnrichmentState.PausedByBudget => "Waiting for provider capacity.",
            LocationEnrichmentState.PausedByAuthority => authority.AvailabilitySummary,
            LocationEnrichmentState.BackingOff => "Waiting for a bounded retry.",
            _ => null
        };
        var noAction = canStart || active || canResume || canRetry || canRepair ? null
            : !authority.Available ? authority.AvailabilitySummary
            : progress.FutureDue > 0 ? "Deferred work is not due yet."
            : progress.ManualRetryAvailable > 0 ? "Some locations require an explicit retry."
            : progress.CannotBeRetried > 0 ? "Some locations have invalid coordinates and cannot be retried."
            : "No eligible enrichment work is available.";
        return new(state.ToString(), workflow?.IntentEnabled ?? false, pausedReason,
            progress.NextAttemptAtUtc ?? workflow?.NextEligibleAtUtc,
            workflow?.ProcessedCount ?? 0, workflow?.EnrichedCount ?? 0,
            workflow?.SkippedCount ?? 0, workflow?.RetryableDeferredCount ?? 0,
            progress.ManualRetryAvailable, progress.CannotBeRetried, progress.IncompleteProviderAddresses,
            workflow?.FailedBatchCount ?? 0,
            progress.RunnableRemaining, progress.FutureDue,
            progress.RunnableRemaining + progress.FutureDue + progress.ManualRetryAvailable + progress.CannotBeRetried,
            workflow?.Outcome.ToString() ?? LocationEnrichmentOutcome.None.ToString(),
            authority.ProviderKey, authority.ProviderDisplayName, authority.Available,
            authority.AvailabilitySummary, authority.GuardEnabled, authority.Usage,
            authority.Limit, authority.Unit, authority.WindowDescription,
            authority.NextAvailableAtUtc, progress.ManualRetryAvailable > 0, noAction,
            Start: new(canStart, canStart),
            Pause: new(active, active), Resume: new(canResume, canResume),
            Cancel: new(active || resumable, active || resumable), RetryDeferred: new(canRetry, canRetry),
            RepairIncomplete: new(progress.IncompleteProviderAddresses > 0, canRepair));
    }
}

public sealed record LocationEnrichmentActionPresentation(bool Visible, bool Enabled);

public sealed record LocationEnrichmentAuthorityPresentation(string? ProviderKey, string ProviderDisplayName,
    bool Available, string AvailabilitySummary, bool GuardEnabled, int Usage, int Limit, string Unit,
    string WindowDescription, DateTime? NextAvailableAtUtc);

public sealed record LocationEnrichmentProgressPresentation(int RunnableRemaining, int FutureDue,
    int ManualRetryAvailable, int CannotBeRetried, DateTime? NextAttemptAtUtc,
    int IncompleteProviderAddresses = 0);

public sealed record LocationEnrichmentPresentationModel(string StatusText, bool IntentEnabled, string? PausedReason,
    DateTime? NextAttemptAtUtc, int Processed, int Enriched, int Skipped, int RetryableDeferred,
    int ManualRetryAvailable, int CannotBeRetried, int IncompleteProviderAddresses, int FailedBatches,
    int RunnableRemaining, int FutureDue, int TotalOutstanding,
    string LastOutcome, string? ProviderKey, string ProviderDisplayName, bool ProviderAvailable,
    string ProviderAvailabilitySummary, bool GuardEnabled, int ProviderUsage, int ProviderLimit,
    string UsageUnit, string UsageWindowDescription, DateTime? ProviderNextAvailableAtUtc,
    bool DeferredWorkRetryable, string? NoActionReason,
    LocationEnrichmentActionPresentation Start, LocationEnrichmentActionPresentation Pause,
    LocationEnrichmentActionPresentation Resume, LocationEnrichmentActionPresentation Cancel,
    LocationEnrichmentActionPresentation RetryDeferred, LocationEnrichmentActionPresentation RepairIncomplete);
