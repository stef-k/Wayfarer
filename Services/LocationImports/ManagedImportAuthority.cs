using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services.LocationImports;

/// <summary>One short lock order: optional OS gate, installation reservation, account deletion fence, account, custody, import.</summary>
internal static class ManagedImportAuthority
{
    internal static async Task<IDbContextTransaction> BeginAsync(ApplicationDbContext db, string? userId, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) throw new ManagedImportException(503, "managed_store_unavailable");
        var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '2s'", token);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(887, 1)", token);
            if (userId is not null)
            {
                await LocationDeletionFence.AcquireAsync(db, userId, token);
                _ = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(token);
            }
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    /// <summary>Bounds a complete control/database/finalization operation, including waits across several commands.</summary>
    internal static CancellationTokenSource Budget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        return budget;
    }

    internal static Task<DateTime> NowAsync(ApplicationDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    internal static Task<MobileLocationImportUpload?> FindAsync(ApplicationDbContext db, Guid id, CancellationToken token) =>
        db.Set<MobileLocationImportUpload>().SingleOrDefaultAsync(row => row.Id == id, token);

    /// <summary>Fresh primary-DB credential/account validation holds token revocation until this mutation ends.</summary>
    internal static async Task RequireCredentialAsync(ApplicationDbContext db, string userId, string credential, CancellationToken token)
    {
        var hash = ApiTokenService.HashToken(credential);
        var tokens = await db.ApiTokens.FromSqlInterpolated($"""
            SELECT * FROM "ApiTokens" WHERE "UserId" = {userId}
            AND ("TokenHash" = {hash} OR ("Token" = {credential} AND lower(btrim("Name")) <> 'mapbox')) FOR SHARE
            """).AsNoTracking().ToListAsync(token);
        if (tokens.Count == 0 || !await db.Users.AnyAsync(row => row.Id == userId && row.IsActive, token))
            throw new ManagedImportException(401, "unauthorized");
    }

    internal static bool Live(MobileLocationImportUpload? row, int generation, Guid lease, DateTime now) =>
        row is { UserId: not null, AcceptedAtUtc: null, FileState: ManagedImportFileState.Uploading }
        && row.Generation == generation && row.LeaseId == lease && row.LeaseExpiresAtUtc > now && row.UploadDeadlineUtc > now;

    internal static void Remember(MobileLocationImportUpload row, LocationImport import)
    {
        row.LastState = import.Status.Value;
        row.LastProcessedRecords = import.LastProcessedIndex;
        row.LastTotalRecords = import.TotalRecords > 0 || import.Status == ImportStatus.Completed ? import.TotalRecords : null;
        row.LastExecutionEpoch = import.ExecutionEpoch;
    }
}

/// <summary>Fixed privacy-safe failure, translated at both early middleware and MVC boundaries.</summary>
public sealed class ManagedImportException(int status, string code) : Exception("Location import request could not be completed.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
