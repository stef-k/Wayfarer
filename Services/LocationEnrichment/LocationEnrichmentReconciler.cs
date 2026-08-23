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
    private const int PageSize = 200;

    /// <summary>Recovers running rows, repairs active triggers, and removes orphan jobs without contact.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var now = db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                .SingleAsync(cancellationToken)
            : DateTime.UtcNow;
        var triggerKeys = (await scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken)).ToHashSet();
        var orphanCandidates = (await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken)).ToHashSet();
        string? afterUserId = null;
        while (true)
        {
            var page = await db.LocationEnrichmentWorkflows
                .Where(item => afterUserId == null || string.Compare(item.UserId, afterUserId) > 0)
                .OrderBy(item => item.UserId).Take(PageSize).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            foreach (var workflow in page.Where(item => item.State == LocationEnrichmentState.Running))
                workflow.RecoverRunning(now);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var workflow in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                orphanCandidates.Remove(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId));
                await schedulerOwner.EnsureScheduledAsync(workflow, triggerKeys, cancellationToken);
            }
            afterUserId = page[^1].UserId;
            db.ChangeTracker.Clear();
        }
        foreach (var orphan in orphanCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await scheduler.DeleteJob(orphan, cancellationToken);
        }
    }
}
