using Microsoft.EntityFrameworkCore;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Adapts the existing bounded provider-authority backfill into one Quartz execution.</summary>
public sealed class LocationEnrichmentWorker(
    ApplicationDbContext db, ILocationEnrichmentBatch batch, IWorkflowScheduleProjection schedule) : ILocationEnrichmentWorker
{
    public async Task RunBatchAsync(string userId, int epoch, CancellationToken cancellationToken)
    {
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == userId, cancellationToken);
        if (workflow.State != LocationEnrichmentState.Running || workflow.Epoch != epoch) return;
        var result = await batch.RunAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        workflow.RecordBatch(result.Scanned, result.Succeeded, result.Unavailable, result.NoResult,
            result.Scanned, now);
        if (result.RemainingEstimate == 0)
            workflow.TransitionToTerminal(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoCandidates, now);
        else if (result.Exhausted)
            workflow.ContinueAs(LocationEnrichmentState.PausedByBudget,
                LocationEnrichmentOutcome.BudgetExhausted, now.AddMinutes(5), now);
        else
            workflow.ContinueAs(LocationEnrichmentState.Scheduled, LocationEnrichmentOutcome.None, now, now);
        await db.SaveChangesAsync(cancellationToken);
        await schedule.ProjectAsync(userId, cancellationToken);
    }
}

/// <summary>Common bounded primitive implemented by the existing protected backfill owner.</summary>
public interface ILocationEnrichmentBatch
{
    Task<GeoapifyBackfillResult> RunAsync(string userId, CancellationToken cancellationToken = default);
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
