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
        cancellationToken.ThrowIfCancellationRequested();
        var now = db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                .SingleAsync(cancellationToken)
            : DateTime.UtcNow;
        var triggerKeys = (await scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken)).ToHashSet();
        var jobKeys = (await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken)).ToHashSet();
        var orphanCandidates = jobKeys.ToHashSet();
        await RecoverExpiredAttemptsAsync(now, cancellationToken);
        string? afterUserId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await db.LocationEnrichmentWorkflows
                .Where(item => afterUserId == null || string.Compare(item.UserId, afterUserId) > 0)
                .OrderBy(item => item.UserId).Take(PageSize).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            foreach (var workflow in page.Where(item => item.State == LocationEnrichmentState.Running))
                workflow.TryRecoverExpiredExecution(now);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var workflow in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                orphanCandidates.Remove(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId));
                await schedulerOwner.EnsureScheduledAsync(workflow, jobKeys, triggerKeys, cancellationToken);
            }
            afterUserId = page[^1].UserId;
            db.ChangeTracker.Clear();
        }
        foreach (var orphan in orphanCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSchedulerId(orphan, out var schedulerId)) continue;
            if (await db.LocationEnrichmentWorkflows.AsNoTracking()
                .AnyAsync(item => item.SchedulerId == schedulerId, cancellationToken)) continue;
            await scheduler.DeleteJob(orphan, cancellationToken);
        }
    }

    private async Task RecoverExpiredAttemptsAsync(DateTime now, CancellationToken cancellationToken)
    {
        long afterId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempts = await db.LocationEnrichmentAttempts
                .Where(item => item.Id > afterId && item.OperationId != null && item.NextAttemptAtUtc <= now)
                .OrderBy(item => item.Id).Take(PageSize).ToListAsync(cancellationToken);
            if (attempts.Count == 0) return;
            foreach (var attempt in attempts)
            {
                attempt.OperationId = null;
                attempt.OperationLeaseId = null;
                attempt.OperationFencingGeneration = null;
                attempt.OperationStartedAtUtc = null;
                attempt.OperationWorkflowEpoch = null;
                attempt.OperationAttemptNumber = null;
                attempt.Outcome = LocationEnrichmentOutcome.RetryableFailure;
            }
            await db.SaveChangesAsync(cancellationToken);
            afterId = attempts[^1].Id;
            db.ChangeTracker.Clear();
        }
    }

    private static bool TryReadSchedulerId(JobKey key, out Guid schedulerId)
    {
        const string prefix = "Workflow_";
        schedulerId = default;
        return key.Group == LocationEnrichmentScheduler.Group
            && key.Name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.Name[prefix.Length..], "N", out schedulerId);
    }
}
