using Microsoft.EntityFrameworkCore;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Adapts the existing bounded provider-authority backfill into one Quartz execution.</summary>
public sealed class LocationEnrichmentWorker(
    IDbContextFactory<ApplicationDbContext> contexts, LocationEnrichmentExecutionAuthority authority,
    ILocationEnrichmentBatch batch, IWorkflowScheduleProjection schedule, SseService? sse = null) : ILocationEnrichmentWorker
{
    public async Task<LocationEnrichmentWorkerOutcome> RunBatchAsync(
        string userId, int epoch, CancellationToken cancellationToken)
    {
        var owner = await authority.TryAcquireAsync(userId, epoch, cancellationToken);
        if (!owner.HasValue) return LocationEnrichmentWorkerOutcome.AuthorityUnavailable;
        GeoapifyBackfillResult result;
        try { result = await batch.RunAsync(owner.Value, cancellationToken); }
        catch (OperationCanceledException)
        {
            await authority.TryReleaseAsync(owner.Value, CancellationToken.None);
            return LocationEnrichmentWorkerOutcome.Cancelled;
        }
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(CancellationToken.None) : null;
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, CancellationToken.None);
        var workflow = db.Database.IsNpgsql()
            ? await db.LocationEnrichmentWorkflows.FromSqlInterpolated($$"""
                SELECT *, xmin FROM "LocationEnrichmentWorkflows" WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleAsync(CancellationToken.None)
            : await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == userId, CancellationToken.None);
        if (workflow.Epoch != epoch || !workflow.HasExecutionLease(owner.Value.LeaseId,
                owner.Value.FencingGeneration, now))
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            return LocationEnrichmentWorkerOutcome.StaleOwner;
        }
        workflow.RecordBatch(result.Scanned, result.Succeeded, result.Unavailable, result.NoResult, 0, now);
        workflow.ReplaceProgress(workflow.ProcessedCount, workflow.EnrichedCount, workflow.SkippedCount,
            workflow.RetryableDeferredCount, workflow.PermanentlyDeferredCount, result.RemainingEstimate,
            workflow.FailedBatchCount, now);
        if (result.AuthorityUnavailable)
            workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, now);
        else if (result.RemainingEstimate == 0)
            workflow.TransitionToTerminal(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoCandidates, now);
        else if (result.Exhausted)
        {
            var wake = result.NextEligibleAt?.UtcDateTime;
            workflow.ContinueAs(wake.HasValue ? LocationEnrichmentState.PausedByBudget : LocationEnrichmentState.Scheduled,
                LocationEnrichmentOutcome.BudgetExhausted, wake ?? now, now);
        }
        else if (result.Unavailable > 0)
        {
            var next = result.NextEligibleAt?.UtcDateTime ?? await db.LocationEnrichmentAttempts.Where(item => item.UserId == userId
                    && item.Outcome == LocationEnrichmentOutcome.RetryableFailure)
                .MinAsync(item => (DateTime?)item.NextAttemptAtUtc, cancellationToken) ?? now;
            workflow.ContinueAs(LocationEnrichmentState.BackingOff,
                LocationEnrichmentOutcome.RetryableFailure, next, now);
        }
        else
            workflow.ContinueAs(LocationEnrichmentState.Scheduled, LocationEnrichmentOutcome.None, now, now);
        workflow.TryReleaseExecutionLease(owner.Value.LeaseId, owner.Value.FencingGeneration);
        await db.SaveChangesAsync(CancellationToken.None);
        if (transaction != null) await transaction.CommitAsync(CancellationToken.None);
        if (sse is not null)
            await sse.BroadcastAsync($"import-{userId}", "{\"type\":\"enrichment-state\"}");
        await schedule.ProjectAsync(userId, CancellationToken.None);
        return LocationEnrichmentWorkerOutcome.Completed;
    }
}

/// <summary>Common bounded primitive implemented by the existing protected backfill owner.</summary>
public interface ILocationEnrichmentBatch
{
    Task<GeoapifyBackfillResult> RunAsync(LocationEnrichmentExecutionLease owner,
        CancellationToken cancellationToken = default);
}

/// <summary>Projects only already-committed workflow state into Quartz.</summary>
public interface IWorkflowScheduleProjection
{
    Task ProjectAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Loads committed state in a fresh scope before changing Quartz metadata.</summary>
public sealed class WorkflowScheduleProjection(
    ApplicationDbContext db, LocationEnrichmentScheduler scheduler) : IWorkflowScheduleProjection
{
    public async Task ProjectAsync(string userId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleAsync(item => item.UserId == userId, cancellationToken);
        await scheduler.EnsureScheduledAsync(workflow, cancellationToken);
    }
}
