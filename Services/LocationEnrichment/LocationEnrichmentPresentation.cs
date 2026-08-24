using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Maps durable workflow facts to the complete user command presentation.</summary>
public static class LocationEnrichmentPresentation
{
    public static LocationEnrichmentPresentationModel Build(LocationEnrichmentWorkflow? workflow,
        bool providerAvailable = true, bool guardAvailable = true)
    {
        var state = workflow?.State ?? LocationEnrichmentState.Idle;
        var active = state is LocationEnrichmentState.Scheduled or LocationEnrichmentState.Running
            or LocationEnrichmentState.PausedByBudget or LocationEnrichmentState.BackingOff;
        var resumable = state is LocationEnrichmentState.PausedByUser or LocationEnrichmentState.PausedByAuthority;
        return new(state.ToString(), state switch
        {
            LocationEnrichmentState.PausedByUser => "Paused by you",
            LocationEnrichmentState.PausedByBudget => "Waiting for provider guard capacity",
            LocationEnrichmentState.PausedByAuthority => "Provider authority is unavailable",
            LocationEnrichmentState.BackingOff => "Waiting for a bounded retry",
            _ => null
        }, workflow?.NextEligibleAtUtc, workflow?.ProcessedCount ?? 0, workflow?.EnrichedCount ?? 0,
            workflow?.SkippedCount ?? 0, workflow?.RetryableDeferredCount ?? 0,
            workflow?.PermanentlyDeferredCount ?? 0, workflow?.FailedBatchCount ?? 0,
            workflow?.RemainingEligibleCount ?? 0, providerAvailable, guardAvailable,
            Start: new(!active && !resumable, providerAvailable && guardAvailable),
            Pause: new(active, active), Resume: new(resumable, resumable && providerAvailable && guardAvailable),
            Cancel: new(active || resumable, active || resumable),
            RetryDeferred: new(!active && (workflow?.PermanentlyDeferredCount ?? 0) > 0,
                !active && providerAvailable && guardAvailable));
    }
}

public sealed record LocationEnrichmentActionPresentation(bool Visible, bool Enabled);

public sealed record LocationEnrichmentPresentationModel(string StatusText, string? PausedReason,
    DateTime? NextAttemptAtUtc, int Processed, int Enriched, int Skipped, int RetryableDeferred,
    int PermanentlyDeferred, int FailedBatches, int RemainingEligible, bool ProviderAvailable,
    bool GuardAvailable, LocationEnrichmentActionPresentation Start, LocationEnrichmentActionPresentation Pause,
    LocationEnrichmentActionPresentation Resume, LocationEnrichmentActionPresentation Cancel,
    LocationEnrichmentActionPresentation RetryDeferred);
