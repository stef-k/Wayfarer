using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Irreversible DB claim, then independently gated exact unlink and confirmed reservation release.</summary>
public sealed class ManagedImportCleanup(IDbContextFactory<ApplicationDbContext> contexts,
    LocationImportStagedFiles files, ManagedImportStorageGate gate, IScheduler scheduler,
    ILogger<ManagedImportCleanup> logger)
{
    internal IManagedImportObserver? Observer { get; set; }

    /// <summary>Bounded indexed startup/recurring work. Reads never call this maintenance owner.</summary>
    public async Task MaintainAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await ManagedImportAuthority.NowAsync(db, token);
        var ids = await db.Set<MobileLocationImportUpload>().AsNoTracking()
            .Where(row => row.RetryAtUtc != null && row.RetryAtUtc <= now)
            .OrderBy(row => row.RetryAtUtc).ThenBy(row => row.Id)
            .Take(100).Select(row => row.Id).ToListAsync(token);
        foreach (var id in ids) await ProcessAsync(id, token);
    }

    /// <summary>Scheduler/quiescence uncertainty defers; no scheduler/network work occurs while holding the gate.</summary>
    public async Task ProcessAsync(Guid id, CancellationToken token)
    {
        try
        {
            var snapshot = await LoadAsync(id, token);
            if (snapshot is null) return;
            if (snapshot.ImportId is int importId && !await QuiesceAsync(importId, token)) return;
            var claim = await ClaimAsync(id, token);
            if (claim is null) return;
            using var storage = gate.TryAcquire(); // Claim transaction has already committed and released every DB lock.
            if (storage is null) { await DeferAsync(claim, "storage_exclusion_unavailable", token); return; }
            await RemoveAsync(claim, storage.StoreIdentity, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ManagedImportException or DbUpdateException or Npgsql.NpgsqlException)
        {
            logger.LogWarning("Managed import cleanup deferred; code {Code}.", "managed-import-cleanup-pending");
            // Unknown finalization leaves its exact claim/reservation intact. Retrying never recreates bytes.
        }
    }

    private async Task<bool> QuiesceAsync(int importId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var import = await db.LocationImports.AsNoTracking().SingleOrDefaultAsync(row => row.Id == importId, token);
        if (import is null) return true;
        if (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping) return false;
        try
        {
            if ((await scheduler.GetCurrentlyExecutingJobs(token)).Any(job =>
                LocationImportReconciler.TryParseJob(job.JobDetail.Key, out var id, out _) && id == importId)) return false;
            var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), token);
            foreach (var key in keys.Where(key => LocationImportReconciler.TryParseJob(key, out var id, out _) && id == importId))
                _ = await scheduler.DeleteJob(key, token);
            return !(await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), token))
                .Any(key => LocationImportReconciler.TryParseJob(key, out var id, out _) && id == importId);
        }
        catch (SchedulerException) { return false; }
    }

    private async Task<MobileLocationImportUpload?> LoadAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Set<MobileLocationImportUpload>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, token);
    }

    /// <summary>Claim/publication/Start all serialize on the same authority; cleanup state never reverses.</summary>
    private async Task<MobileLocationImportUpload?> ClaimAsync(Guid id, CancellationToken token)
    {
        var snapshot = await LoadAsync(id, token);
        if (snapshot is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, snapshot.UserId, token);
        var row = await db.Set<MobileLocationImportUpload>().Include(item => item.Import).SingleOrDefaultAsync(item => item.Id == id, token);
        if (row is null) return null;
        var now = await ManagedImportAuthority.NowAsync(db, token);
        var import = row.Import;
        if (import is not null)
        {
            if (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping) return null;
            ManagedImportAuthority.Remember(row, import);
            if (import.Status == ImportStatus.Completed && row.CleanupEligibleAtUtc is null)
                row.CleanupEligibleAtUtc = now.AddHours(24); // Conservative later authoritative observation, never mtime.
            if (row.FileState == ManagedImportFileState.Removed)
            {
                await RetireAsync(db, row, now, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return null;
            }
            if (!import.DeletionRequestedAtUtc.HasValue && (import.Status != ImportStatus.Completed || row.CleanupEligibleAtUtc > now))
            {
                // Retry only near eligibility, so staged/stopped rows cannot starve bounded maintenance pages.
                row.RetryAtUtc = row.CleanupEligibleAtUtc ?? now.AddMinutes(5);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return null;
            }
        }
        if (row.FileState == ManagedImportFileState.Uploading && row.LeaseExpiresAtUtc > now && row.UploadDeadlineUtc > now) return null;
        if (row.FileState == ManagedImportFileState.Removed) return null;
        if (row.FileState == ManagedImportFileState.CleanupClaimed && row.LeaseExpiresAtUtc > now) return null;
        if (import is null && row.AcceptedAtUtc.HasValue && row.CleanupReason != "account_deleted")
            return null; // Missing import is never affirmative unlink authority.
        row.FileState = ManagedImportFileState.CleanupClaimed;
        row.CleanupReason ??= import?.DeletionRequestedAtUtc is not null ? "import_deleted"
            : import is not null ? "completed" : "upload_expired";
        row.LeaseId = Guid.NewGuid();
        row.LeaseExpiresAtUtc = now.AddSeconds(60);
        row.RetryAtUtc = now.AddSeconds(60);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return row;
    }

    private async Task RemoveAsync(MobileLocationImportUpload claim, string identity, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, claim.UserId, token);
        await ManagedImportUpload.RequireStoreAsync(db, identity, token);
        var row = await db.Set<MobileLocationImportUpload>().Include(item => item.Import).SingleOrDefaultAsync(item => item.Id == claim.Id, token);
        var now = await ManagedImportAuthority.NowAsync(db, token);
        if (row is null || row.Generation != claim.Generation || row.FileReference != claim.FileReference
            || row.FileState != ManagedImportFileState.CleanupClaimed || row.LeaseId != claim.LeaseId || row.LeaseExpiresAtUtc <= now) return;
        if (row.Import is { } import && (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping)) return;
        if (!files.TryResolve(row.FileReference, out var path)) throw new ManagedImportException(503, "managed_store_unavailable");
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // Absence converges only inside this gate with fresh authority.
        if (Observer is not null) await Observer.AfterUnlinkAsync(token);
        row.FileState = ManagedImportFileState.Removed;
        row.FileReference = null;
        row.ReservedBytes = row.VerifiedBytes = 0;
        row.LeaseId = null;
        row.LeaseExpiresAtUtc = null;
        row.UploadDeadlineUtc = null;
        row.RetryAtUtc = null;
        row.FailureCode = null;
        if (row.UserId is null) db.Remove(row);
        else await RetireAsync(db, row, now, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    /// <summary>History retirement waits for unresolved handoff, even though file-only cleanup may finish.</summary>
    private static Task RetireAsync(ApplicationDbContext db, MobileLocationImportUpload row, DateTime now, CancellationToken token)
    {
        var import = row.Import;
        if (import is null) { row.RetryAtUtc = null; return Task.CompletedTask; }
        row.RetryAtUtc = import.EnrichmentHandoffPending ? now.AddMinutes(5)
            : row.CleanupEligibleAtUtc?.AddDays(89);
        if (import.EnrichmentHandoffPending) return Task.CompletedTask;
        if (import.DeletionRequestedAtUtc.HasValue || (import.Status == ImportStatus.Completed
            && row.CleanupEligibleAtUtc <= now.AddDays(-89)))
        {
            ManagedImportAuthority.Remember(row, import);
            row.RetiredAtUtc = now;
            row.RetryAtUtc = null;
            row.Import = null;
            row.ImportId = null;
            db.Remove(import);
        }
        return Task.CompletedTask;
    }

    private async Task DeferAsync(MobileLocationImportUpload claim, string code, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, claim.UserId, token);
        var row = await ManagedImportAuthority.FindAsync(db, claim.Id, token);
        if (row is null || row.Generation != claim.Generation || row.LeaseId != claim.LeaseId) return;
        row.FailureCode = code;
        row.RetryAtUtc = (await ManagedImportAuthority.NowAsync(db, token)).AddMinutes(5);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
