using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Repairs bounded Quartz projections from authoritative relational workflow rows.</summary>
public sealed class LocationEnrichmentReconciler(
    ApplicationDbContext db, LocationEnrichmentScheduler schedulerOwner, IScheduler scheduler)
{
    /// <summary>Recovers running rows, repairs active triggers, and removes orphan jobs without contact.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var workflows = await db.LocationEnrichmentWorkflows.ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var workflow in workflows.Where(item => item.State == LocationEnrichmentState.Running))
            workflow.RecoverRunning(now);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var workflow in workflows)
            await schedulerOwner.EnsureScheduledAsync(workflow, cancellationToken);
        var valid = workflows.Select(item => LocationEnrichmentScheduler.JobKey(item.SchedulerId)).ToHashSet();
        var quartzKeys = await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken);
        foreach (var orphan in quartzKeys.Where(item => !valid.Contains(item)))
            await scheduler.DeleteJob(orphan, cancellationToken);
    }
}
