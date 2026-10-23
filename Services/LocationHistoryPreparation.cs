using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Operator-owned, all-owner preparation with short atomic progress and independent activation.</summary>
internal sealed class LocationHistoryPreparation(ApplicationDbContext db)
{
    internal const string WriterVersion = "history-writers-1";

    /// <summary>Starts declared maintenance for selected generations; ordinary prepare resumes existing state.</summary>
    internal async Task BeginAsync(string kind, bool rebuild, CancellationToken token)
    {
        ValidateKind(kind);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"history-preparation:" + kind}, 0))", token);
        var state = await db.HistoryPreparationStates.SingleOrDefaultAsync(row => row.Kind == kind, token);
        var version = Version(kind);
        if (state == null)
        {
            state = new HistoryPreparationState { Kind = kind, TargetVersion = version };
            db.Add(state); rebuild = true;
        }
        else if (state.TargetVersion != version && !rebuild)
            throw new InvalidOperationException("Rule identity changed; explicitly rebuild the affected generation.");
        if (rebuild)
        {
            state.TargetVersion = version; state.ActiveVersion = null; state.Ready = false; state.Scanning = true;
            state.LastId = 0; state.Examined = 0; state.Prepared = 0; state.LastFailure = null;
            state.WriterVersion = WriterVersion; state.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        db.ChangeTracker.Clear();
    }

    /// <summary>Commits at most 500 locked final rows and their checkpoint together; cancellation rolls the batch back.</summary>
    internal async Task<bool> PrepareBatchAsync(string kind, CancellationToken token, int batchSize = 500)
    {
        ValidateKind(kind);
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", token);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '60s'", token);
            var state = await db.HistoryPreparationStates.FromSqlInterpolated($$"""
                SELECT * FROM "HistoryPreparationStates" WHERE "Kind" = {{kind}} FOR UPDATE
                """).SingleAsync(token);
            if (state.TargetVersion != Version(kind) || state.WriterVersion != WriterVersion)
                throw new InvalidOperationException("Preparation target does not match this binary.");
            if (state.Ready) { await transaction.CommitAsync(token); return true; }
            var version = Version(kind);
            var rows = state.Scanning
                ? await db.Locations.FromSqlInterpolated($$"""
                    SELECT * FROM "Locations" WHERE "Id" > {{state.LastId}} ORDER BY "Id" LIMIT {{batchSize}} FOR UPDATE
                    """).AsNoTracking().ToListAsync(token)
                : kind == "time"
                    ? await db.Locations.FromSqlInterpolated($$"""
                        SELECT * FROM "Locations" WHERE "HistoryTimeVersion" IS DISTINCT FROM {{version}}
                        ORDER BY "Id" LIMIT {{batchSize}} FOR UPDATE
                        """).AsNoTracking().ToListAsync(token)
                    : await db.Locations.FromSqlInterpolated($$"""
                        SELECT * FROM "Locations" WHERE "HistoryTextVersion" IS DISTINCT FROM {{version}}
                        ORDER BY "Id" LIMIT {{batchSize}} FOR UPDATE
                        """).AsNoTracking().ToListAsync(token);
            foreach (var row in rows)
                await LocationHistoryStorage.UpdateAsync(db, row, kind == "time", kind == "text", token);
            state.Examined += rows.Count; state.Prepared += rows.Count;
            if (state.Scanning && rows.Count > 0) state.LastId = rows[^1].Id;
            if (rows.Count == 0)
            {
                if (state.Scanning) state.Scanning = false;
                else
                {
                    if (!await IndexesReadyAsync(token)) throw new InvalidOperationException("Required history indexes are unavailable.");
                    state.Ready = true; state.ActiveVersion = version;
                }
            }
            state.LastFailure = null; state.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            var complete = state.Ready;
            db.ChangeTracker.Clear();
            return complete;
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            // A failed/cancelled batch never advances a checkpoint or stamps rows unresolved.
            // The independent failure write cannot hide the primary exception if the store is unavailable.
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await db.HistoryPreparationStates.Where(row => row.Kind == kind).ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.LastFailure, exception is OperationCanceledException ? "cancelled: resume prepare" : "batch failed: verify database, indexes and rules; resume prepare")
                    .SetProperty(row => row.Ready, false), deadline.Token);
            }
            catch (Exception) { /* Preserve the primary failure; previously committed progress remains authoritative. */ }
            throw;
        }
    }

    /// <summary>Fresh all-owner counts and physical prerequisites; callers use their response transaction snapshot.</summary>
    internal async Task<HistoryPreparationStatus> StatusAsync(string kind, CancellationToken token)
    {
        ValidateKind(kind);
        var state = await db.HistoryPreparationStates.AsNoTracking().SingleOrDefaultAsync(row => row.Kind == kind, token);
        var version = Version(kind);
        var scanning = state?.Scanning ?? true;
        var lastId = state?.LastId ?? 0;
        var all = await db.Locations.LongCountAsync(token);
        var pending = kind == "time"
            ? await db.Locations.LongCountAsync(row => row.HistoryTimeVersion != version || row.HistoryTimeVersion == null
                || scanning && row.Id > lastId, token)
            : await db.Locations.LongCountAsync(row => row.HistoryTextVersion != version || row.HistoryTextVersion == null
                || scanning && row.Id > lastId, token);
        var unresolved = kind == "time" ? await db.Locations.LongCountAsync(row => row.HistoryTimeVersion == version
            && row.HistoryTimeStatus == "unresolved", token) : 0;
        var indexes = await IndexesReadyAsync(token);
        var ready = state?.Ready == true && state.ActiveVersion == version && state.TargetVersion == version
            && state.WriterVersion == WriterVersion && pending == 0 && indexes;
        return new(kind, version, state?.ActiveVersion, ready, all, pending, unresolved, state?.Examined ?? 0,
            state?.Prepared ?? 0, state?.LastId ?? 0, indexes, state?.LastFailure);
    }

    /// <summary>Checks valid/ready physical index definitions, not just names, and the required trigram extension.</summary>
    internal Task<bool> IndexesReadyAsync(CancellationToken token) => db.Database.SqlQueryRaw<bool>("""
        SELECT EXISTS (SELECT FROM pg_extension WHERE extname = 'pg_trgm') AND
            (SELECT count(*) = 4 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
             JOIN pg_am am ON am.oid = c.relam
             JOIN (VALUES
               ('IX_Location_HistoryUtc', 'btree', 3, 6, '"UserId", "LocalTimestamp", "Id", "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId"'),
               ('IX_Location_HistoryCapture', 'btree', 3, 3, '"UserId", "CaptureLocalTimestamp", "Id"'),
               ('IX_Location_HistoryText', 'gin', 1, 1, '"HistorySearchText"'),
               ('IX_Location_HistoryGeometry', 'gist', 1, 1, '("Coordinates"::geometry)')
             ) expected(name, method, keys, attributes, columns) ON c.relname = expected.name
             WHERE i.indrelid = '"Locations"'::regclass AND i.indisvalid AND i.indisready AND i.indpred IS NULL
               AND am.amname = expected.method AND i.indnkeyatts = expected.keys AND i.indnatts = expected.attributes
               AND (SELECT string_agg(pg_get_indexdef(i.indexrelid, n, true), ', ' ORDER BY n)
                    FROM generate_series(1, i.indnatts) n) = expected.columns
               AND (c.relname <> 'IX_Location_HistoryText' OR EXISTS
                    (SELECT FROM pg_opclass op WHERE op.oid = i.indclass[0] AND op.opcname = 'gin_trgm_ops')))
            AS "Value"
        """).SingleAsync(token);

    /// <summary>Only binary-pinned rules can be prepared; no arbitrary operator-supplied version labels.</summary>
    private static string Version(string kind) => kind == "time" ? LocationHistoryDerivation.TimeVersion : LocationHistoryDerivation.TextVersion;
    private static void ValidateKind(string kind)
    {
        if (kind is not ("time" or "text")) throw new ArgumentException("Select time or text.", nameof(kind));
    }
}

/// <summary>Safe operational progress; pending work is separate from classified exceptional time evidence.</summary>
internal sealed record HistoryPreparationStatus(string Kind, string TargetVersion, string? ActiveVersion, bool Ready,
    long Total, long Pending, long Unresolved, long Examined, long Prepared, int LastId, bool IndexesReady, string? LastFailure);
