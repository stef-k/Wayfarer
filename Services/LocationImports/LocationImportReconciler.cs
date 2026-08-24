using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Repairs the bounded import-specific Quartz projection without provider contact.</summary>
public sealed class LocationImportReconciler(
    ApplicationDbContext db, IScheduler scheduler, ILogger<LocationImportReconciler> logger)
{
    private const int PageSize = 100;

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var executing = (await scheduler.GetCurrentlyExecutingJobs(cancellationToken))
            .Select(item => item.JobDetail.Key).ToHashSet();
        var projected = (await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), cancellationToken) ?? []).ToHashSet();
        var triggers = (await scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals(LocationImportSchedulerKeys.Group), cancellationToken) ?? []).ToHashSet();
        var authorities = new Dictionary<JobKey, (int ImportId, int Epoch, ImportStatus Status)>();
        var afterId = 0;
        while (true)
        {
            var page = await db.LocationImports.AsNoTracking().Where(item => item.Id > afterId)
                .OrderBy(item => item.Id).Take(PageSize)
                .Select(item => new { item.Id, item.ExecutionEpoch, item.Status }).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                var key = LocationImportSchedulerKeys.Job(item.Id, item.ExecutionEpoch);
                authorities[key] = (item.Id, item.ExecutionEpoch, item.Status);
                if (await IsDeletionPendingAsync(item.Id, cancellationToken))
                    await FinalizeDeletionAsync(item.Id, projected, executing, cancellationToken);
                else if (item.Status == ImportStatus.InProgress &&
                    (!projected.Contains(key) || !triggers.Contains(LocationImportSchedulerKeys.Trigger(item.Id, item.ExecutionEpoch))))
                    await RepairAsync(item.Id, item.ExecutionEpoch, cancellationToken);
                else if (item.Status == ImportStatus.Stopping)
                {
                    if (executing.Contains(key)) _ = await scheduler.Interrupt(key, cancellationToken);
                    else await FinalizeStoppedAsync(item.Id, item.ExecutionEpoch, cancellationToken);
                }
                else if (item.Status != ImportStatus.InProgress && projected.Contains(key) && !executing.Contains(key))
                    await scheduler.DeleteJob(key, cancellationToken);
            }
            afterId = page[^1].Id;
        }

        foreach (var key in projected.Where(key => !executing.Contains(key)))
        {
            if (!authorities.TryGetValue(key, out var authority) || key != LocationImportSchedulerKeys.Job(authority.ImportId, authority.Epoch))
            {
                try { await scheduler.DeleteJob(key, cancellationToken); }
                catch (SchedulerException exception) { logger.LogWarning(exception, "Stale import projection {JobKey} remains for retry.", key); }
            }
        }
    }

    private Task<bool> IsDeletionPendingAsync(int importId, CancellationToken token) => db.LocationImports.AsNoTracking()
        .AnyAsync(item => item.Id == importId && item.DeletionRequestedAtUtc != null, token);

    private async Task FinalizeDeletionAsync(int importId, HashSet<JobKey> projected, HashSet<JobKey> executing,
        CancellationToken token)
    {
        var matching = projected.Where(key => key.Name.StartsWith($"LocationImportJob_{importId}_", StringComparison.Ordinal)).ToList();
        if (matching.Any(executing.Contains)) return;
        foreach (var key in matching) await scheduler.DeleteJob(key, token);
        db.ChangeTracker.Clear();
        var import = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId && item.DeletionRequestedAtUtc != null, token);
        if (import is null) return;
        if (File.Exists(import.FilePath)) File.Delete(import.FilePath);
        db.LocationImports.Remove(import);
        await db.SaveChangesAsync(token);
    }

    private async Task RepairAsync(int importId, int epoch, CancellationToken token)
    {
        var owner = new LocationImportLifecycle(db, scheduler, NullLogger<LocationImportLifecycle>.Instance);
        try
        {
            await owner.EnsureProjectionAsync(importId, epoch, token);
            db.ChangeTracker.Clear();
            var current = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, token);
            if (current is not null && current.ExecutionEpoch == epoch && current.Status == ImportStatus.InProgress)
            {
                current.ProjectionPending = false;
                await db.SaveChangesAsync(token);
            }
        }
        catch (SchedulerException exception) { logger.LogWarning(exception, "Import {ImportId} projection repair remains pending.", importId); }
    }

    private async Task FinalizeStoppedAsync(int importId, int epoch, CancellationToken token)
    {
        db.ChangeTracker.Clear();
        var current = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, token);
        if (current is null || current.ExecutionEpoch != epoch || current.Status != ImportStatus.Stopping) return;
        current.Status = ImportStatus.Stopped;
        current.ProjectionPending = false;
        await db.SaveChangesAsync(token);
        var key = LocationImportSchedulerKeys.Job(importId, epoch);
        if (await scheduler.CheckExists(key, token)) await scheduler.DeleteJob(key, token);
    }
}
