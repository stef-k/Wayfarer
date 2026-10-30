using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Bounded indexed receipt cleanup; never retries mutations or removes ingestion deletion markers.</summary>
internal sealed record LocationMutationCleanupCursor(DateTime RetainUntilUtc, string Owner, long Id);

/// <summary>Bounded indexed receipt cleanup using the existing Quartz job's durable scan position.</summary>
internal sealed class LocationMutationCleanup(ApplicationDbContext db)
{
    /// <summary>Advances past busy candidates, with at most 200 rows per owner transaction and 20 transactions/10 seconds per run.</summary>
    internal async Task<(int Deleted, bool FollowUp, LocationMutationCleanupCursor? Cursor)> RunAsync(CancellationToken token, LocationMutationCleanupCursor? cursor = null)
    {
        var elapsed = Stopwatch.StartNew();
        var transactions = 0;
        var deleted = 0;
        DateTime? afterTime = cursor?.RetainUntilUtc;
        long afterId = cursor?.Id ?? 0;
        string afterOwner = cursor?.Owner ?? "";
        while (transactions < 20 && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var now = await LocationMutationService.NowAsync(db, token);
            var page = await db.LocationMutationOperations.AsNoTracking()
                .Where(row => row.ReceiptRetainUntilUtc <= now && row.ExpiresAtUtc <= now
                    && (afterTime == null || row.ReceiptRetainUntilUtc > afterTime
                        || row.ReceiptRetainUntilUtc == afterTime && (string.Compare(row.UserId, afterOwner) > 0
                            || row.UserId == afterOwner && row.Id > afterId)))
                .OrderBy(row => row.ReceiptRetainUntilUtc).ThenBy(row => row.UserId).ThenBy(row => row.Id)
                .Select(row => new { row.Id, row.UserId, row.ReceiptRetainUntilUtc }).Take(200).ToListAsync(token);
            if (page.Count == 0) return (deleted, false, null);
            for (var offset = 0; offset < page.Count;)
            {
                if (transactions >= 20 || elapsed.Elapsed >= TimeSpan.FromSeconds(10)) break;
                var owner = page[offset].UserId;
                var batch = page.Skip(offset).TakeWhile(row => row.UserId == owner).ToArray();
                offset += batch.Length;
                var last = batch[^1]; afterTime = last.ReceiptRetainUntilUtc; afterOwner = last.UserId; afterId = last.Id;
                transactions++;
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                try
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1s'; SET LOCAL statement_timeout = '2s'", token);
                    var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({"location-deletion:" + owner}, 838)) AS \"Value\"").SingleAsync(token);
                    if (!acquired) continue;
                    var ids = batch.Select(row => row.Id).ToArray();
                    var removed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
                        DELETE FROM "LocationMutationOperations" WHERE "UserId" = {{owner}} AND "Id" = ANY({{ids}})
                        AND "ReceiptRetainUntilUtc" <= clock_timestamp() AND "ExpiresAtUtc" <= clock_timestamp()
                        """, token);
                    await transaction.CommitAsync(token);
                    deleted += removed;
                }
                catch (PostgresException error) when (error.SqlState is "55P03" or "57014")
                { /* Defer busy rows; periodic scheduling retains restart/outage recovery. */ }
            }
        }
        var observed = await LocationMutationService.NowAsync(db, token);
        return (deleted, await db.LocationMutationOperations.AnyAsync(row => row.ReceiptRetainUntilUtc <= observed && row.ExpiresAtUtc <= observed, token),
            afterTime == null ? null : new(afterTime.Value, afterOwner, afterId));
    }
}
