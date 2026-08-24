using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Repairs the bounded import-specific Quartz projection without provider contact.</summary>
public sealed class LocationImportReconciler(
    IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler, ILogger<LocationImportReconciler> logger)
{
    private const int PageSize = 100;

    public async Task ReconcileAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var executing = (await scheduler.GetCurrentlyExecutingJobs(token)).Select(x => x.JobDetail.Key).ToHashSet();
        var projected = (await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), token) ?? [])
            .Where(key => TryParseJob(key, out _, out _)).ToHashSet();
        var triggers = (await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(LocationImportSchedulerKeys.Group), token) ?? [])
            .Where(key => TryParseTrigger(key, out _, out _)).ToHashSet();
        var orphans = projected.ToHashSet();
        var afterId = 0;
        while (true)
        {
            var page = await LoadPageAsync(afterId, token);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                token.ThrowIfCancellationRequested();
                var key = LocationImportSchedulerKeys.Job(item.Id, item.Epoch);
                orphans.Remove(key);
                if (item.DeletionRequestedAtUtc.HasValue)
                    await FinalizeDeletionAsync(item.Id, projected, executing, token);
                else if (item.Status == ImportStatus.InProgress &&
                    (!projected.Contains(key) || !triggers.Contains(LocationImportSchedulerKeys.Trigger(item.Id, item.Epoch))))
                    await RepairAsync(item.Id, item.Epoch, projected, triggers, token);
                else if (item.Status == ImportStatus.Stopping)
                    await ConvergeStopAsync(item.Id, item.Epoch, key, executing, projected, token);
                else if (item.Status != ImportStatus.InProgress && projected.Contains(key) && !executing.Contains(key))
                    await DeleteProjectionAsync(key, projected, token);
            }
            afterId = page[^1].Id;
        }
        foreach (var orphan in orphans.Where(key => projected.Contains(key) && !executing.Contains(key)))
            await DeleteOrRepairOrphanAsync(orphan, projected, triggers, token);
    }

    private async Task<List<Authority>> LoadPageAsync(int afterId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().Where(x => x.Id > afterId).OrderBy(x => x.Id).Take(PageSize)
            .Select(x => new Authority(x.Id, x.ExecutionEpoch, x.Status, x.DeletionRequestedAtUtc)).ToListAsync(token);
    }

    private async Task<Authority?> LoadAuthorityAsync(int importId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().Where(x => x.Id == importId)
            .Select(x => new Authority(x.Id, x.ExecutionEpoch, x.Status, x.DeletionRequestedAtUtc)).SingleOrDefaultAsync(token);
    }

    private async Task FinalizeDeletionAsync(int importId, HashSet<JobKey> projected, HashSet<JobKey> executing,
        CancellationToken token)
    {
        var authority = await LoadAuthorityAsync(importId, token);
        if (authority is null || !authority.DeletionRequestedAtUtc.HasValue
            || authority.Status == ImportStatus.InProgress || authority.Status == ImportStatus.Stopping) return;
        var matching = projected.Where(key => TryParseJob(key, out var id, out _) && id == importId).ToList();
        if (matching.Any(executing.Contains)) return;
        foreach (var key in matching)
        {
            var cleanup = await DeleteProjectionAsync(key, projected, token);
            if (cleanup is not QuartzCleanupResult.Removed and not QuartzCleanupResult.AlreadyAbsent) return;
        }
        await using var db = await contexts.CreateDbContextAsync(token);
        var import = await db.LocationImports.SingleOrDefaultAsync(
            x => x.Id == importId && x.DeletionRequestedAtUtc != null, token);
        if (import is null || import.ExecutionEpoch != authority.Epoch
            || import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping) return;
        if (File.Exists(import.FilePath)) File.Delete(import.FilePath);
        db.LocationImports.Remove(import);
        await db.SaveChangesAsync(token);
    }

    private async Task RepairAsync(int importId, int epoch, HashSet<JobKey> projected,
        HashSet<TriggerKey> triggers, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await new LocationImportLifecycle(db, scheduler, NullLogger<LocationImportLifecycle>.Instance)
                .EnsureProjectionAsync(importId, epoch, token);
            projected.Add(LocationImportSchedulerKeys.Job(importId, epoch));
            triggers.Add(LocationImportSchedulerKeys.Trigger(importId, epoch));
        }
        catch (Exception exception) when (exception is SchedulerException or ObjectAlreadyExistsException)
        {
            logger.LogWarning(exception, "Import {ImportId} projection repair remains pending.", importId);
            return;
        }
        await using var verification = await contexts.CreateDbContextAsync(token);
        var current = await verification.LocationImports.SingleOrDefaultAsync(x => x.Id == importId, token);
        if (current is not null && current.ExecutionEpoch == epoch && current.Status == ImportStatus.InProgress)
        {
            current.ProjectionPending = false;
            await verification.SaveChangesAsync(token);
        }
    }

    private async Task ConvergeStopAsync(int importId, int epoch, JobKey key, HashSet<JobKey> executing,
        HashSet<JobKey> projected, CancellationToken token)
    {
        if (executing.Contains(key)) { _ = await scheduler.Interrupt(key, token); return; }
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var current = await db.LocationImports.SingleOrDefaultAsync(x => x.Id == importId, token);
            if (current is null || current.ExecutionEpoch != epoch || current.Status != ImportStatus.Stopping) return;
            current.Status = ImportStatus.Stopped;
            current.ProjectionPending = false;
            await db.SaveChangesAsync(token);
        }
        if (projected.Contains(key)) await DeleteProjectionAsync(key, projected, token);
    }

    private async Task DeleteOrRepairOrphanAsync(JobKey key, HashSet<JobKey> projected,
        HashSet<TriggerKey> triggers, CancellationToken token)
    {
        if (!TryParseJob(key, out var importId, out _)) return;
        var authority = await LoadAuthorityAsync(importId, token);
        if (authority is null || authority.DeletionRequestedAtUtc.HasValue || authority.Status != ImportStatus.InProgress)
        { await DeleteProjectionAsync(key, projected, token); return; }
        var currentKey = LocationImportSchedulerKeys.Job(importId, authority.Epoch);
        if (key != currentKey) await DeleteProjectionAsync(key, projected, token);
        await RepairAsync(importId, authority.Epoch, projected, triggers, token);
    }

    private async Task<QuartzCleanupResult> DeleteProjectionAsync(
        JobKey key, HashSet<JobKey> projected, CancellationToken token)
    {
        try
        {
            var removed = await scheduler.DeleteJob(key, token);
            projected.Remove(key);
            return removed ? QuartzCleanupResult.Removed : QuartzCleanupResult.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return QuartzCleanupResult.Cancelled;
        }
        catch (SchedulerException exception)
        {
            logger.LogWarning(exception, "Import projection {JobKey} remains for retry.", key);
            return QuartzCleanupResult.SchedulerFailed;
        }
    }

    internal static bool TryParseJob(JobKey key, out int importId, out int epoch) =>
        TryParse(key.Group, key.Name, "LocationImportJob_", out importId, out epoch);
    internal static bool TryParseTrigger(TriggerKey key, out int importId, out int epoch) =>
        TryParse(key.Group, key.Name, "LocationImportTrigger_", out importId, out epoch);

    private static bool TryParse(string group, string name, string prefix, out int importId, out int epoch)
    {
        importId = epoch = 0;
        if (group != LocationImportSchedulerKeys.Group || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var values = name[prefix.Length..].Split('_');
        return values.Length == 2
            && int.TryParse(values[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out importId) && importId > 0
            && int.TryParse(values[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 0
            && name == $"{prefix}{importId}_{epoch}";
    }

    private sealed record Authority(int Id, int Epoch, ImportStatus Status, DateTime? DeletionRequestedAtUtc);

    private enum QuartzCleanupResult { Removed, AlreadyAbsent, SchedulerFailed, Cancelled }
}
