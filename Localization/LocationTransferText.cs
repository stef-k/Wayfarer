using System.Globalization;
using Wayfarer.Models;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Localization;

/// <summary>Projects legacy and bounded transfer values into Web text without changing their stored or API forms.</summary>
public static class LocationTransferText
{
    /// <summary>Returns the badge and catalog identity for the import's durable status.</summary>
    public static (string Key, string Badge) ImportStatusDisplay(ImportStatus status) => status.Value switch
    {
        "In Progress" => ("Transfers.State.InProgress", "bg-info"),
        "Stopping" => ("Transfers.State.Stopping", "bg-secondary"),
        "Stopped" => ("Transfers.State.Stopped", "bg-dark"),
        "Completed" => ("Transfers.State.Completed", "bg-success"),
        "Failed" => ("Transfers.State.Failed", "bg-danger"),
        _ => ("Transfers.Unavailable", "bg-secondary")
    };

    /// <summary>Maps managed file custody to its display identity.</summary>
    public static string CustodyKey(string? state) => state switch
    {
        ManagedImportFileState.Retained => "Transfers.PayloadRetained",
        ManagedImportFileState.Removed => "Transfers.PayloadRemoved",
        ManagedImportFileState.CleanupClaimed => "Transfers.PayloadCleanup",
        ManagedImportFileState.Uploading => "Transfers.PayloadUploading",
        _ => "Transfers.Unavailable"
    };

    /// <summary>Uses known worker failures and safe historical fallbacks for older rows.</summary>
    public static string? ImportFailureKey(LocationImport import) => import.FailureCode switch
    {
        "staged_file_unavailable" => "Transfers.StagedUnavailable",
        "location-import-recovery-held" => "Transfers.RecoveryHeld",
        "location-import-invalid-idempotency-key" => "Transfers.InvalidKey",
        "location-import-multiple-zero-idempotency-keys" => "Transfers.MultipleZeroKeys",
        "location-import-checkpoint-incompatible" => "Transfers.InvalidCheckpoint",
        null when import.ErrorMessage == "Import staged file unavailable." => "Transfers.StagedUnavailable",
        null when import.ErrorMessage == "Native recovery content is incomplete or unsupported. Correct the file before importing." => "Transfers.RecoveryHeld",
        null when import.ErrorMessage == "Import checkpoint no longer matches parsed records. Upload the file as a new import to continue." => "Transfers.InvalidCheckpoint",
        null when import.Status != ImportStatus.Failed && string.IsNullOrWhiteSpace(import.ErrorMessage) => null,
        _ => "Transfers.ImportProcessingFailed"
    };

    /// <summary>Localizes the fixed UTC checkpoint prefix and preserves the stored address verbatim.</summary>
    public static string LastRecord(WebMessages text, string? record)
    {
        const string prefix = "Timestamp: ";
        const int timestampLength = 20;
        if (string.IsNullOrWhiteSpace(record) || record == "N/A") return text["Transfers.Unavailable"];
        if (!record.StartsWith(prefix, StringComparison.Ordinal) || record.Length < prefix.Length + timestampLength
            || !DateTime.TryParseExact(record.AsSpan(prefix.Length, timestampLength), "u", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            return record;

        var date = UtcDate(text, timestamp);
        var suffix = record[(prefix.Length + timestampLength)..];
        if (suffix.Length == 0) return text["Transfers.LastRecordTimestamp", date];
        return suffix.StartsWith(", ", StringComparison.Ordinal)
            ? text["Transfers.LastRecordWithAddress", date, suffix[2..]]
            : record;
    }

    /// <summary>Formats the existing UTC display time in the document locale.</summary>
    public static string UtcDate(WebMessages text, DateTime timestamp) => $"{timestamp.ToString("g", text.DisplayCulture)} UTC";

    /// <summary>Uses the existing provider authority projection to choose the visible explanation.</summary>
    public static string AvailabilityKey(LocationEnrichmentPresentationModel model) => model.BudgetExhausted
        ? "Transfers.BudgetExhausted"
        : model.CredentialAuthorityCategory switch
        {
            "Admitted" => "Transfers.AuthorityCurrent",
            "NoProviderSelected" => "Transfers.NoProvider",
            "Unauthorized" => "Transfers.ProviderUnauthorized",
            "Unverified" => "Transfers.ProviderUnverified",
            "ConsentRequired" => "Transfers.ProviderConsent",
            "CredentialUnavailable" => "Transfers.CredentialUnavailable",
            _ => "Transfers.ProviderUnavailable"
        };

    /// <summary>Translates the projected hold or no-action reason without deriving action availability again.</summary>
    public static string EnrichmentReasonKey(LocationEnrichmentPresentationModel model) =>
        (model.PausedReason ?? model.NoActionReason) switch
        {
            null => "Transfers.ChooseAction",
            "Paused by you." => "Transfers.UserHold",
            "Waiting for provider capacity." => "Transfers.ProviderHold",
            "Waiting for a bounded retry." => "Transfers.RetryHold",
            "Deferred work is not due yet." => "Transfers.DeferredNotDue",
            "Some locations require an explicit retry." => "Transfers.ExplicitRetry",
            "Some locations have invalid coordinates and cannot be retried." => "Transfers.InvalidCoordinates",
            "No eligible enrichment work is available." => "Transfers.NoCandidates",
            var reason when reason == model.ProviderAvailabilitySummary => AvailabilityKey(model),
            _ => "Transfers.EnrichmentUnavailable"
        };

    /// <summary>Translates the finite Web control outcomes while leaving bearer API codes intact.</summary>
    public static string EnrichmentConflictKey(string code) => code switch
    {
        "authority-unavailable" => "Transfers.RetryAuthority",
        "budget-exhausted" => "Transfers.BudgetExhausted",
        "no-candidates" => "Transfers.NoCandidates",
        "missing-workflow" => "Transfers.NoWorkflow",
        "control-unavailable" => "Transfers.EnrichmentUnavailable",
        _ => "Transfers.EnrichmentConflict"
    };
}
