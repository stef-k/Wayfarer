using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Describes durable discharge of a committed import checkpoint; Running is always pending.</summary>
public enum EnrichmentHandoffDisposition { Pending, ContinuationAccepted, Held, NoOutstandingWork }

/// <summary>Names the four account-wide bearer controls; retry and repair remain Web tools.</summary>
public enum EnrichmentControl { Start, Pause, Resume, Cancel }

/// <summary>Requires an observed workflow snapshot and the bearer credential revalidated at mutation.</summary>
public sealed record EnrichmentCommandCondition(int ExpectedEpoch, LocationEnrichmentState ExpectedState, string Credential);

/// <summary>Owns implicit continuation separately from explicit user commands.</summary>
public sealed partial class ImportEnrichmentHandoff
{
    /// <summary>Preserves holds and deadlines under the same locks used by commands and worker acquisition.</summary>
    public async Task<EnrichmentHandoffDisposition> EnsureAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await LockWorkflowCreationAuthorityAsync(userId, cancellationToken);
        var workflow = await LockWorkflowAsync(userId, cancellationToken);
        if (workflow?.State is LocationEnrichmentState.PausedByUser or LocationEnrichmentState.Cancelled
            or LocationEnrichmentState.Failed or LocationEnrichmentState.PausedByAuthority)
            return EnrichmentHandoffDisposition.Held;
        if (workflow?.State == LocationEnrichmentState.Running || workflow?.ExecutionLeaseId is not null)
            return EnrichmentHandoffDisposition.Pending;
        if (workflow?.State is LocationEnrichmentState.Scheduled or LocationEnrichmentState.PausedByBudget
            or LocationEnrichmentState.BackingOff)
        {
            if (!workflow.IntentEnabled || workflow.NextEligibleAtUtc is null) return EnrichmentHandoffDisposition.Pending;
        }
        else
        {
            var inspection = await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken);
            var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
            if (inspection.Category != PersonalProviderAdmissionCategory.Admitted || inspection.Binding is null)
            {
                workflow ??= AddWorkflow(userId, now);
                workflow.Start(now);
                workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, now);
            }
            else
            {
                if (!await AuthorityIsCurrentAsync(userId, inspection.Binding, cancellationToken))
                    return EnrichmentHandoffDisposition.Pending;
                var progress = await progressQuery.ProjectAsync(userId, inspection.Binding, now, cancellationToken);
                if (progress.InFlight > 0 || progress.AwaitingRecovery > 0) return EnrichmentHandoffDisposition.Pending;
                if (progress.RunnableRemaining == 0 && progress.FutureDue == 0)
                    return progress.ManualRetryAvailable > 0 ? EnrichmentHandoffDisposition.Pending
                        : EnrichmentHandoffDisposition.NoOutstandingWork;
                if (progress.RunnableRemaining == 0 && progress.NextAttemptAtUtc is null)
                    return EnrichmentHandoffDisposition.Pending;
                workflow ??= AddWorkflow(userId, now);
                workflow.Start(now);
                if (inspection.Exhausted)
                    workflow.ContinueAs(LocationEnrichmentState.PausedByBudget, LocationEnrichmentOutcome.BudgetExhausted,
                        BudgetWake(inspection, now), now);
                else if (progress.RunnableRemaining == 0)
                {
                    workflow.ContinueAs(LocationEnrichmentState.BackingOff, LocationEnrichmentOutcome.RetryableFailure,
                        progress.NextAttemptAtUtc, now);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        if (transaction is not null) await transaction.DisposeAsync();
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentHandoffDisposition.Pending; }
        return workflow.State == LocationEnrichmentState.PausedByAuthority ? EnrichmentHandoffDisposition.Held
            : EnrichmentHandoffDisposition.ContinuationAccepted;
    }

    private LocationEnrichmentWorkflow AddWorkflow(string userId, DateTime now)
    {
        var workflow = LocationEnrichmentWorkflow.Create(userId, now);
        db.Add(workflow);
        return workflow;
    }

    /// <summary>Uses provider-native shared retry policy, including its five-second safety delay.</summary>
    private static DateTime BudgetWake(PersonalProviderInspection inspection, DateTime now) =>
        inspection.ProviderKey == "mapbox" ? LocationEnrichmentRetryPolicy.MapboxWake(new(now)).UtcDateTime
            : inspection.NextAvailableAt?.UtcDateTime ?? LocationEnrichmentRetryPolicy.GeoapifyWake(new(now), []).UtcDateTime;

    /// <summary>Routes conditional adapters into the same locked commands as Web.</summary>
    public Task<EnrichmentCommandResult> ControlAsync(string userId, EnrichmentControl control,
        EnrichmentCommandCondition condition, CancellationToken cancellationToken = default) => control switch
    {
        EnrichmentControl.Start => StartCoreAsync(userId, condition, cancellationToken),
        EnrichmentControl.Pause => PauseCoreAsync(userId, condition, cancellationToken),
        EnrichmentControl.Resume => ResumeCoreAsync(userId, condition, cancellationToken),
        EnrichmentControl.Cancel => CancelCoreAsync(userId, condition, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    /// <summary>Orders creation/account, token and workflow locks before checking conditional state.</summary>
    private async Task<LocationEnrichmentWorkflow?> LockCommandAsync(string userId,
        EnrichmentCommandCondition? condition, CancellationToken token)
    {
        await LockWorkflowCreationAuthorityAsync(userId, token);
        if (condition is not null) await ManagedImportAuthority.RequireCredentialAsync(db, userId, condition.Credential, token);
        return await LockWorkflowAsync(userId, token);
    }

    private static bool Matches(EnrichmentCommandCondition? condition, LocationEnrichmentWorkflow? workflow) =>
        condition is null || condition.ExpectedEpoch == (workflow?.Epoch ?? 0)
            && condition.ExpectedState == (workflow?.State ?? LocationEnrichmentState.Idle);

    private static EnrichmentCommandResult Unavailable(PersonalProviderInspection inspection) =>
        inspection.Category == PersonalProviderAdmissionCategory.Admitted && inspection.Exhausted
            ? EnrichmentCommandResult.Conflict("budget-exhausted") : EnrichmentCommandResult.Authority("authority-unavailable");
}
