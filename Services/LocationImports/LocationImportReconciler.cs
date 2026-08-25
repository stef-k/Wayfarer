using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Repairs the bounded import-specific Quartz projection without provider contact.</summary>
public sealed class LocationImportReconciler(
    IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler, ILogger<LocationImportReconciler> logger,
    LocationImportProjectionCoordinator? projectionCoordinator = null)
{
    private const int PageSize = 100;
    private readonly LocationImportProjectionCoordinator projectionCoordinator =
        projectionCoordinator ?? LocationImportProjectionCoordinator.Shared;

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
                await using var projection = await projectionCoordinator.AcquireAsync(item.Id, token);
                var authority = await LoadAuthorityAsync(item.Id, token);
                if (authority is null) continue;
                var key = LocationImportSchedulerKeys.Job(item.Id, authority.Epoch);
                orphans.Remove(key);
                if (authority.DeletionRequestedAtUtc.HasValue)
                    await FinalizeDeletionAsync(authority.Id, projected, executing, token);
                else if (authority.Status == ImportStatus.InProgress &&
                    (!projected.Contains(key) || !triggers.Contains(LocationImportSchedulerKeys.Trigger(item.Id, authority.Epoch))))
                    await RepairAsync(authority.Id, authority.Epoch, projected, triggers, token);
                else if (authority.Status == ImportStatus.Stopping)
                    await ConvergeStopAsync(authority.Id, authority.Epoch, key, executing, projected, token);
                else if (authority.Status != ImportStatus.InProgress && projected.Contains(key) && !executing.Contains(key))
                    await DeleteProjectionAsync(key, projected, token);
            }
            afterId = page[^1].Id;
        }
        foreach (var orphan in orphans.Where(key => projected.Contains(key) && !executing.Contains(key)))
        {
            if (!TryParseJob(orphan, out var importId, out _)) continue;
            await using var projection = await projectionCoordinator.AcquireAsync(importId, token);
            await DeleteOrRepairOrphanAsync(orphan, projected, triggers, token);
        }
    }

    private async Task<List<Authority>> LoadPageAsync(int afterId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().Where(x => x.Id > afterId).OrderBy(x => x.Id).Take(PageSize)
            .Select(x => new Authority(x.Id, x.ExecutionEpoch, x.Status, x.StopRequestedAtUtc,
                x.DeletionRequestedAtUtc)).ToListAsync(token);
    }

    private async Task<Authority?> LoadAuthorityAsync(int importId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().Where(x => x.Id == importId)
            .Select(x => new Authority(x.Id, x.ExecutionEpoch, x.Status, x.StopRequestedAtUtc,
                x.DeletionRequestedAtUtc)).SingleOrDefaultAsync(token);
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
        var remaining = await scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), token) ?? [];
        if (remaining.Any(key => TryParseJob(key, out var id, out _) && id == importId)) return;
        string? path;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            path = await db.LocationImports.AsNoTracking()
                .Where(x => x.Id == importId && x.DeletionRequestedAtUtc != null
                    && x.ExecutionEpoch == authority.Epoch && x.Status != ImportStatus.InProgress
                    && x.Status != ImportStatus.Stopping)
                .Select(x => x.FilePath).SingleOrDefaultAsync(token);
        }
        if (path is null) return;
        if (File.Exists(path)) File.Delete(path);
        await using var deletion = await contexts.CreateDbContextAsync(token);
        var import = await deletion.LocationImports.SingleOrDefaultAsync(
            x => x.Id == importId && x.DeletionRequestedAtUtc != null, token);
        if (import is null || import.ExecutionEpoch != authority.Epoch
            || import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping) return;
        deletion.LocationImports.Remove(import);
        await deletion.SaveChangesAsync(token);
    }

    private async Task RepairAsync(int importId, int epoch, HashSet<JobKey> projected,
        HashSet<TriggerKey> triggers, CancellationToken token)
    {
        var authority = await LoadAuthorityAsync(importId, token);
        if (!AllowsProjection(authority, epoch))
        {
            await DeleteProjectionAsync(LocationImportSchedulerKeys.Job(importId, epoch), projected, token);
            if (authority is not null && AllowsProjection(authority, authority.Epoch))
                await ProjectAsync(importId, authority.Epoch, projected, triggers, token);
            return;
        }
        try
        {
            await ProjectAsync(importId, epoch, projected, triggers, token);
        }
        catch (Exception exception) when (exception is SchedulerException or ObjectAlreadyExistsException)
        {
            logger.LogWarning(exception, "Import {ImportId} projection repair remains pending.", importId);
            return;
        }
        authority = await LoadAuthorityAsync(importId, token);
        if (!AllowsProjection(authority, epoch))
        {
            await DeleteProjectionAsync(LocationImportSchedulerKeys.Job(importId, epoch), projected, token);
            if (authority is not null && AllowsProjection(authority, authority.Epoch))
                await ProjectAsync(importId, authority.Epoch, projected, triggers, token);
            if (authority?.DeletionRequestedAtUtc.HasValue == true)
                await FinalizeDeletionAsync(importId, projected, [], token);
            return;
        }
        await using var verification = await contexts.CreateDbContextAsync(token);
        var current = await verification.LocationImports.SingleOrDefaultAsync(x => x.Id == importId, token);
        if (current is not null && current.ExecutionEpoch == epoch && current.Status == ImportStatus.InProgress
            && !current.StopRequestedAtUtc.HasValue && !current.DeletionRequestedAtUtc.HasValue)
        {
            current.ProjectionPending = false;
            await verification.SaveChangesAsync(token);
        }
    }

    private async Task ProjectAsync(int importId, int epoch, HashSet<JobKey> projected,
        HashSet<TriggerKey> triggers, CancellationToken token)
    {
        await new LocationImportLifecycle(contexts, scheduler, NullLogger<LocationImportLifecycle>.Instance,
                projectionCoordinator)
            .EnsureProjectionAsync(importId, epoch, token);
        projected.Add(LocationImportSchedulerKeys.Job(importId, epoch));
        triggers.Add(LocationImportSchedulerKeys.Trigger(importId, epoch));
    }

    private static bool AllowsProjection(Authority? authority, int epoch) =>
        authority is not null && authority.Epoch == epoch && authority.Status == ImportStatus.InProgress
        && !authority.StopRequestedAtUtc.HasValue && !authority.DeletionRequestedAtUtc.HasValue;

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
        catch (SchedulerException)
        {
            logger.LogWarning("Import projection {JobKey} remains for retry.", key);
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

    private sealed record Authority(int Id, int Epoch, ImportStatus Status, DateTime? StopRequestedAtUtc,
        DateTime? DeletionRequestedAtUtc);

    private enum QuartzCleanupResult { Removed, AlreadyAbsent, SchedulerFailed, Cancelled }
}
