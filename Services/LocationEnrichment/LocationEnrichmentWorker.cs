using Microsoft.EntityFrameworkCore;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Adapts the existing bounded provider-authority backfill into one Quartz execution.</summary>
public sealed class LocationEnrichmentWorker(
    ApplicationDbContext db, ILocationEnrichmentBatch batch, IWorkflowScheduleProjection schedule,
    SseService? sse = null) : ILocationEnrichmentWorker
{
    public async Task RunBatchAsync(string userId, int epoch, CancellationToken cancellationToken)
    {
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == userId, cancellationToken);
        if (workflow.State != LocationEnrichmentState.Running || workflow.Epoch != epoch) return;
        var result = await batch.RunAsync(userId, epoch, cancellationToken);
        await db.Entry(workflow).ReloadAsync(CancellationToken.None);
        if (workflow.State != LocationEnrichmentState.Running || workflow.Epoch != epoch) return;
        var now = DateTime.UtcNow;
        workflow.RecordBatch(result.Scanned, result.Succeeded, result.Unavailable, result.NoResult,
            result.Scanned, now);
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
            var next = await db.LocationEnrichmentAttempts.Where(item => item.UserId == userId
                    && item.Outcome == LocationEnrichmentOutcome.RetryableFailure)
                .MinAsync(item => (DateTime?)item.NextAttemptAtUtc, cancellationToken) ?? now;
            workflow.ContinueAs(LocationEnrichmentState.BackingOff,
                LocationEnrichmentOutcome.RetryableFailure, next, now);
        }
        else
            workflow.ContinueAs(LocationEnrichmentState.Scheduled, LocationEnrichmentOutcome.None, now, now);
        await db.SaveChangesAsync(cancellationToken);
        if (sse is not null)
            await sse.BroadcastAsync($"import-{userId}", "{\"type\":\"enrichment-state\"}");
        await schedule.ProjectAsync(userId, cancellationToken);
    }
}

/// <summary>Common bounded primitive implemented by the existing protected backfill owner.</summary>
public interface ILocationEnrichmentBatch
{
    Task<GeoapifyBackfillResult> RunAsync(string userId, int epoch,
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
