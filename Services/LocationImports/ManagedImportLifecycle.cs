using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Conditional managed commands share the custody lock with cleanup, including calls from Web.</summary>
public sealed class ManagedImportLifecycle(IDbContextFactory<ApplicationDbContext> contexts,
    LocationImportStagedFiles files, IScheduler scheduler, ILogger<LocationImportLifecycle> logger,
    ManagedImportCleanup cleanup)
{
    /// <summary>Null target preserves Web's retained-file restart behavior; explicit targets recover exactly one epoch.</summary>
    public async Task<bool> StartAsync(string userId, int importId, int? targetEpoch, string? credential, CancellationToken token)
    {
        int epoch;
        bool created;
        await using (var db = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await ManagedImportAuthority.BeginAsync(db, userId, token))
        {
            if (credential is not null) await ManagedImportAuthority.RequireCredentialAsync(db, userId, credential, token);
            var custody = await db.Set<MobileLocationImportUpload>().Include(row => row.Import)
                .SingleOrDefaultAsync(row => row.UserId == userId && row.ImportId == importId, token)
                ?? throw new ManagedImportException(404, "import_not_found");
            var import = custody.Import!;
            if (targetEpoch.HasValue && targetEpoch == import.ExecutionEpoch && targetEpoch > 0) return false;
            if (targetEpoch.HasValue && targetEpoch < import.ExecutionEpoch)
                throw new ManagedImportException(409, "execution_superseded");
            if (import.DeletionRequestedAtUtc.HasValue || custody.FileState != ManagedImportFileState.Retained)
                throw new ManagedImportException(409, "file_cleanup_pending_or_removed");
            if (import.Status == ImportStatus.Stopping || (targetEpoch.HasValue && import.Status == ImportStatus.Completed))
                throw new ManagedImportException(409, "invalid_import_transition");
            if (targetEpoch.HasValue && (targetEpoch <= 0 || import.ExecutionEpoch == int.MaxValue
                || targetEpoch != import.ExecutionEpoch + 1 || import.Status == ImportStatus.InProgress))
                throw new ManagedImportException(409, "execution_epoch_conflict");
            if (import.Status == ImportStatus.Failed && import.FailureCode is "location-import-invalid-idempotency-key"
                or "location-import-multiple-zero-idempotency-keys" or "location-import-checkpoint-incompatible")
                throw new ManagedImportException(409, "new_upload_required");
            created = import.Status != ImportStatus.InProgress;
            if (created)
            {
                if (await db.LocationImports.AnyAsync(row => row.UserId == userId && row.ManagedUpload
                    && row.Id != importId && (row.Status == ImportStatus.InProgress || row.Status == ImportStatus.Stopping), token))
                    throw new ManagedImportException(409, "execution_active");
                import.ExecutionEpoch = checked(import.ExecutionEpoch + 1);
                import.Status = ImportStatus.InProgress;
                import.StopRequestedAtUtc = null;
                import.ErrorMessage = import.FailureCode = null;
                custody.CleanupEligibleAtUtc = null;
            }
            import.ProjectionPending = true;
            epoch = import.ExecutionEpoch;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        // Scheduler calls run after all reservation/account/custody locks have been released.
        await ProjectAsync(importId, epoch, token);
        return created;
    }

    /// <summary>Same-epoch terminal Stop is recovery; stale/future Stop never adopts another generation.</summary>
    public async Task<bool> StopAsync(string userId, int importId, int? executionEpoch, string? credential, CancellationToken token)
    {
        int epoch;
        await using (var db = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await ManagedImportAuthority.BeginAsync(db, userId, token))
        {
            if (credential is not null) await ManagedImportAuthority.RequireCredentialAsync(db, userId, credential, token);
            var import = await db.LocationImports.SingleOrDefaultAsync(row => row.Id == importId && row.UserId == userId
                && row.ManagedUpload, token) ?? throw new ManagedImportException(404, "import_not_found");
            epoch = import.ExecutionEpoch;
            if (executionEpoch.HasValue && (executionEpoch <= 0 || executionEpoch != epoch))
                throw new ManagedImportException(409, "execution_epoch_conflict");
            if (import.DeletionRequestedAtUtc.HasValue) throw new ManagedImportException(409, "deletion_pending");
            if (import.Status != ImportStatus.InProgress && import.Status != ImportStatus.Stopping) return false;
            import.Status = ImportStatus.Stopping;
            import.StopRequestedAtUtc ??= await ManagedImportAuthority.NowAsync(db, token);
            import.ProjectionPending = true;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        try { _ = await scheduler.Interrupt(LocationImportSchedulerKeys.Job(importId, epoch), token); }
        catch (SchedulerException) { /* Persisted stop remains recoverable. */ }
        return true;
    }

    /// <summary>Deletes history only after the same file-only cleanup owner confirms removal.</summary>
    public async Task<bool> DeleteAsync(string userId, int importId, string? credential, CancellationToken token)
    {
        Guid id;
        await using (var db = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await ManagedImportAuthority.BeginAsync(db, userId, token))
        {
            if (credential is not null) await ManagedImportAuthority.RequireCredentialAsync(db, userId, credential, token);
            var custody = await db.Set<MobileLocationImportUpload>().Include(row => row.Import)
                .SingleOrDefaultAsync(row => row.UserId == userId && row.ImportId == importId, token)
                ?? throw new ManagedImportException(404, "import_not_found");
            var import = custody.Import!;
            if (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping)
                throw new ManagedImportException(409, "execution_active");
            import.DeletionRequestedAtUtc ??= await ManagedImportAuthority.NowAsync(db, token);
            id = custody.Id;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        await cleanup.ProcessAsync(id, token);
        await using var verification = await contexts.CreateDbContextAsync(token);
        return !await verification.LocationImports.AnyAsync(row => row.Id == importId && row.UserId == userId, token);
    }

    /// <summary>Reuses the existing exact-epoch scheduler projection, rechecking intent on either side.</summary>
    private async Task ProjectAsync(int id, int epoch, CancellationToken token)
    {
        await using var projection = await LocationImportProjectionCoordinator.Shared.AcquireAsync(id, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var import = await db.LocationImports.SingleOrDefaultAsync(row => row.Id == id, token);
        if (import is null || import.ExecutionEpoch != epoch || import.Status != ImportStatus.InProgress
            || import.DeletionRequestedAtUtc.HasValue || import.StopRequestedAtUtc.HasValue) return;
        try
        {
            await new LocationImportLifecycle(files, contexts, scheduler, logger).EnsureProjectionAsync(id, epoch, token);
            import.ProjectionPending = false;
            await db.SaveChangesAsync(token);
        }
        catch (Exception exception) when (exception is SchedulerException or DbUpdateConcurrencyException)
        { /* Durable intent remains the reconciliation owner; no extra epoch on projection uncertainty. */ }
    }
}
