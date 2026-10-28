using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Fixed owner-only persisted projections; no file, parser, scheduler, maintenance or provider work in reads.</summary>
public sealed class ManagedImportStatus(IDbContextFactory<ApplicationDbContext> contexts)
{
    public const string Contract = "mobile-location-file-import-1";

    /// <summary>Finds only this owner's upload identity, including after payload/history removal.</summary>
    public async Task<MobileLocationImportUpload?> UploadAsync(string userId, Guid requestId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        db.Database.SetCommandTimeout(5);
        return await db.Set<MobileLocationImportUpload>().AsNoTracking().Include(row => row.Import)
            .SingleOrDefaultAsync(row => row.UserId == userId && row.RequestId == requestId, token);
    }

    /// <summary>Web-only and foreign jobs have the same absent result.</summary>
    public async Task<MobileLocationImportUpload?> ImportAsync(string userId, int id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        db.Database.SetCommandTimeout(5);
        return await db.Set<MobileLocationImportUpload>().AsNoTracking().Include(row => row.Import)
            .SingleOrDefaultAsync(row => row.UserId == userId && row.ImportId == id, token);
    }

    public static int HttpStatus(MobileLocationImportUpload row) => row.RetiredAtUtc.HasValue ? 410
        : row.AcceptedAtUtc.HasValue ? 200 : row.FileState == ManagedImportFileState.Removed ? 200 : 202;

    /// <summary>Accepted cleanup is always an accepted result; unaccepted cleanup never fabricates an import.</summary>
    public static object Project(MobileLocationImportUpload row)
    {
        var accepted = row.AcceptedAtUtc.HasValue;
        var import = row.Import;
        var file = new
        {
            state = row.FileState switch { ManagedImportFileState.Retained => "retained",
                ManagedImportFileState.CleanupClaimed => "cleanup_pending", ManagedImportFileState.Removed => "removed", _ => "uploading" },
            cleanupEligibleAtUtc = row.CleanupEligibleAtUtc, expiresAtUtc = (DateTime?)null,
            canResume = row.FileState == ManagedImportFileState.Retained && import is not null
                && import.DeletionRequestedAtUtc is null && import.Status != ImportStatus.Completed
                && import.FailureCode is not "location-import-invalid-idempotency-key" and not "location-import-multiple-zero-idempotency-keys"
                    and not "location-import-checkpoint-incompatible"
        };
        object? result = import is null ? null : new
        {
            id = import.Id, state = State(import.Status.Value), executionEpoch = import.ExecutionEpoch,
            processedRecords = import.LastProcessedIndex,
            yieldedTotalRecords = import.TotalRecords > 0 || import.Status == ImportStatus.Completed ? (int?)import.TotalRecords : null,
            skippedDuplicates = import.SkippedDuplicates, skippedDeleted = import.SkippedDeleted,
            unclassifiedActivityCount = import.UnclassifiedActivityCount,
            projectionPending = import.ProjectionPending, stopRequested = import.StopRequestedAtUtc.HasValue,
            deletionPending = import.DeletionRequestedAtUtc.HasValue,
            enrichmentRequested = import.EnrichmentRequested, enrichmentHandoffPending = import.EnrichmentHandoffPending,
            file,
            allowedCommands = import.DeletionRequestedAtUtc.HasValue ? new[] { "delete" }
                : import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping ? ["stop"]
                : file.canResume ? ["start", "delete"] : new[] { "delete" },
            failure = import.Status == ImportStatus.Failed ? Failure(import.FailureCode) : null
        };
        return new
        {
            contract = Contract, subjectId = row.UserId, uploadRequestId = row.RequestId,
            code = row.RetiredAtUtc.HasValue ? "import_upload_retired" : accepted ? "accepted"
                : row.FileState == ManagedImportFileState.Removed ? "retry_required"
                : row.FileState == ManagedImportFileState.CleanupClaimed ? "upload_cleanup_pending" : "upload_in_progress",
            upload = new { state = accepted ? "accepted" : "unaccepted", format = row.Format, byteLength = row.ByteLength,
                sha256 = row.Sha256, enrichmentRequested = row.EnrichmentRequested, acceptedAtUtc = row.AcceptedAtUtc },
            @import = result, file,
            lastResult = row.RetiredAtUtc.HasValue ? new { state = State(row.LastState),
                executionEpoch = row.LastExecutionEpoch, processedRecords = row.LastProcessedRecords, yieldedTotalRecords = row.LastTotalRecords } : null,
            observedAtUtc = DateTime.UtcNow
        };
    }

    private static string State(string? state) => state switch
    {
        "In Progress" => "in_progress", "Stopping" => "stopping", "Stopped" => "stopped",
        "Completed" => "completed", "Failed" => "failed", _ => "unavailable"
    };

    private static object Failure(string? code) => new
    {
        code = code switch
        {
            "location-import-invalid-idempotency-key" or "location-import-multiple-zero-idempotency-keys"
                or "location-import-checkpoint-incompatible" or "staged_file_unavailable" => code,
            _ => "processing_failed"
        },
        message = "Import processing needs attention.",
        retry = code is "location-import-invalid-idempotency-key" or "location-import-multiple-zero-idempotency-keys"
            or "location-import-checkpoint-incompatible" ? "new_upload_required" : "inspect_then_explicit_retry"
    };
}
