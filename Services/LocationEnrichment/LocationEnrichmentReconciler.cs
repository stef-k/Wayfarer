using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Repairs bounded Quartz projections from authoritative relational workflow rows.</summary>
public sealed class LocationEnrichmentReconciler(
    IDbContextFactory<ApplicationDbContext> contexts, LocationEnrichmentScheduler schedulerOwner, IScheduler scheduler)
{
    private const int PageSize = 200;
    private const int ProjectionAttempts = 3;

    /// <summary>Recovers running rows, repairs active triggers, and removes orphan jobs without contact.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = await ReadDatabaseTimeAsync(cancellationToken);
        var triggerKeys = await scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken);
        var triggersByWorkflow = IndexTriggers(triggerKeys);
        var jobKeys = (await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationEnrichmentScheduler.Group), cancellationToken)).ToHashSet();
        var orphanCandidates = jobKeys.ToHashSet();
        await RecoverExpiredAttemptsAsync(now, cancellationToken);
        string? afterUserId = null;
        while (true)
        {
            var page = await LoadWorkflowPageAsync(afterUserId, now, cancellationToken);
            if (page.Count == 0) break;
            foreach (var workflow in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                orphanCandidates.Remove(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId));
                var ownTriggers = triggersByWorkflow.GetValueOrDefault(workflow.SchedulerId) ?? [];
                triggersByWorkflow[workflow.SchedulerId] = ownTriggers;
                await ConvergeProjectionAsync(workflow.SchedulerId, jobKeys, ownTriggers, cancellationToken);
            }
            afterUserId = page[^1].UserId;
        }
        foreach (var orphan in orphanCandidates)
            await DeleteOrRepairOrphanAsync(orphan, jobKeys, triggersByWorkflow, cancellationToken);
    }

    private async Task<DateTime> ReadDatabaseTimeAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                .SingleAsync(cancellationToken)
            : DateTime.UtcNow;
    }

    private async Task<List<LocationEnrichmentWorkflow>> LoadWorkflowPageAsync(
        string? afterUserId, DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var page = await db.LocationEnrichmentWorkflows
            .Where(item => afterUserId == null || string.Compare(item.UserId, afterUserId) > 0)
            .OrderBy(item => item.UserId).Take(PageSize).ToListAsync(cancellationToken);
        foreach (var workflow in page.Where(item => item.State == LocationEnrichmentState.Running))
            workflow.TryRecoverExpiredExecution(now);
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    private async Task ConvergeProjectionAsync(Guid schedulerId, ISet<JobKey> knownJobs,
        ISet<TriggerKey> ownTriggers, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ProjectionAttempts; attempt++)
        {
            var authority = await LoadAuthorityAsync(schedulerId, cancellationToken);
            if (authority is null) return;
            await schedulerOwner.EnsureScheduledAsync(authority, knownJobs, ownTriggers, cancellationToken);
            var current = await LoadAuthorityAsync(schedulerId, cancellationToken);
            if (current is not null && SameProjection(authority, current)) return;
        }
        var final = await LoadAuthorityAsync(schedulerId, cancellationToken);
        if (final is not null)
            await schedulerOwner.EnsureScheduledAsync(final, knownJobs, ownTriggers, cancellationToken);
        var verified = await LoadAuthorityAsync(schedulerId, cancellationToken);
        if (final is null || verified is null || !SameProjection(final, verified))
            throw new InvalidOperationException($"Workflow projection did not converge for {schedulerId:N}.");
    }

    private async Task DeleteOrRepairOrphanAsync(JobKey jobKey, ISet<JobKey> knownJobs,
        IDictionary<Guid, HashSet<TriggerKey>> triggersByWorkflow, CancellationToken cancellationToken)
    {
        if (!TryReadSchedulerId(jobKey, out var schedulerId)) return;
        var authority = await LoadAuthorityAsync(schedulerId, cancellationToken);
        if (authority is null)
        {
            await scheduler.DeleteJob(jobKey, cancellationToken);
            knownJobs.Remove(jobKey);
        }
        authority = await LoadAuthorityAsync(schedulerId, cancellationToken);
        if (authority is null) return;
        if (!triggersByWorkflow.TryGetValue(schedulerId, out var ownTriggers)) ownTriggers = [];
        triggersByWorkflow[schedulerId] = ownTriggers;
        await ConvergeProjectionAsync(schedulerId, knownJobs, ownTriggers, cancellationToken);
    }

    private async Task<LocationEnrichmentWorkflow?> LoadAuthorityAsync(
        Guid schedulerId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SchedulerId == schedulerId, cancellationToken);
    }

    private async Task RecoverExpiredAttemptsAsync(DateTime now, CancellationToken cancellationToken)
    {
        long afterId = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
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
        }
    }

    private static Dictionary<Guid, HashSet<TriggerKey>> IndexTriggers(IEnumerable<TriggerKey> triggerKeys)
    {
        var result = new Dictionary<Guid, HashSet<TriggerKey>>();
        foreach (var key in triggerKeys)
        {
            if (!TryReadTriggerSchedulerId(key, out var schedulerId)) continue;
            if (!result.TryGetValue(schedulerId, out var keys)) result[schedulerId] = keys = [];
            keys.Add(key);
        }
        return result;
    }

    private static bool SameProjection(LocationEnrichmentWorkflow left, LocationEnrichmentWorkflow right) =>
        left.SchedulerId == right.SchedulerId && left.Epoch == right.Epoch && left.State == right.State
        && left.IntentEnabled == right.IntentEnabled && left.NextEligibleAtUtc == right.NextEligibleAtUtc;

    private static bool TryReadSchedulerId(JobKey key, out Guid schedulerId)
    {
        const string prefix = "Workflow_";
        schedulerId = default;
        return key.Group == LocationEnrichmentScheduler.Group
            && key.Name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.Name[prefix.Length..], "N", out schedulerId);
    }

    private static bool TryReadTriggerSchedulerId(TriggerKey key, out Guid schedulerId)
    {
        const string prefix = "Workflow_";
        schedulerId = default;
        if (key.Group != LocationEnrichmentScheduler.Group || !key.Name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var suffix = key.Name[prefix.Length..];
        var separator = suffix.IndexOf('_');
        return separator == 32 && Guid.TryParseExact(suffix[..separator], "N", out schedulerId)
            && int.TryParse(suffix[(separator + 1)..], out var epoch) && epoch >= 0
            && suffix[(separator + 1)..] == epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
