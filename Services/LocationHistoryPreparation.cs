using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Operator-owned, all-owner preparation with short atomic progress and independent activation.</summary>
internal sealed class LocationHistoryPreparation(ApplicationDbContext db)
{
    internal const string WriterVersion = "history-writers-1";

    /// <summary>Starts declared maintenance; ensure selects changed binary generations atomically, while manual prepare only resumes.</summary>
    internal async Task BeginAsync(string kind, bool rebuild, CancellationToken token, bool ensureCurrent = false)
    {
        ValidateKind(kind);
        db.HistoryEnvironment.Require(kind == "time", kind == "text");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"history-preparation:" + kind}, 0))", token);
        var state = await db.HistoryPreparationStates.SingleOrDefaultAsync(row => row.Kind == kind, token);
        var version = Version(kind);
        if (state == null)
        {
            state = new HistoryPreparationState { Kind = kind, TargetVersion = version };
            db.Add(state);
            rebuild = true;
        }
        else if (state.TargetVersion != version || state.WriterVersion != WriterVersion)
        {
            if (ensureCurrent) rebuild = true;
            else if (!rebuild) throw new InvalidOperationException("Rule identity changed; explicitly rebuild the affected generation.");
        }
        if (rebuild)
        {
            state.TargetVersion = version;
            state.ActiveVersion = null;
            state.Ready = false;
            state.Scanning = true;
            state.LastId = 0;
            state.Examined = 0;
            state.Prepared = 0;
            state.LastFailure = null;
            state.WriterVersion = WriterVersion;
            state.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        db.ChangeTracker.Clear();
    }

    /// <summary>Offline target maintenance selects only changed generations and resumes committed batches without resetting progress.</summary>
    internal async Task EnsureAsync(Func<HistoryPreparationStatus, Task>? progress, CancellationToken token)
    {
        db.HistoryEnvironment.Require(true, true);
        foreach (var kind in new[] { "time", "text" }) await BeginAsync(kind, false, token, ensureCurrent: true);
        foreach (var kind in new[] { "time", "text" })
        {
            var status = await StatusAsync(kind, token);
            if (progress is not null) await progress(status);
            if (status.Ready) continue;
            try
            {
                bool complete;
                do
                {
                    complete = await PrepareBatchAsync(kind, token, activate: false);
                    if (progress is not null) await progress(await StatusAsync(kind, token));
                } while (!complete);
                await db.Database.ExecuteSqlRawAsync("ANALYZE \"Locations\"", token);
                // Keep Ready false across process death during ANALYZE, then recheck the stale sweep/indexes before activation.
                if (!await PrepareBatchAsync(kind, token))
                    throw new InvalidOperationException("History rows changed during exclusive finalization; resume maintenance.");
                if (progress is not null) await progress(await StatusAsync(kind, token));
            }
            catch (Exception exception)
            {
                // Final ANALYZE/cancellation failures cannot leave the completed-batch Ready flag accepted.
                await RecordFailureAsync(kind, exception);
                throw;
            }
        }
        if (!(await InspectAsync(token)).All(status => status.Ready))
            throw new InvalidOperationException("History preparation remains incomplete.");
    }

    /// <summary>Independently verifies both generations, physical prerequisites and stale rows in one read-only snapshot.</summary>
    internal async Task<HistoryPreparationStatus[]> InspectAsync(CancellationToken token)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token) : null;
        var statuses = new[] { await StatusAsync("time", token), await StatusAsync("text", token) };
        if (transaction is not null) await transaction.CommitAsync(token);
        return statuses;
    }

    /// <summary>Commits at most 500 final rows/checkpoint; offline ensure defers activation until ANALYZE succeeds.</summary>
    internal async Task<bool> PrepareBatchAsync(string kind, CancellationToken token, int batchSize = 500, bool activate = true)
    {
        ValidateKind(kind);
        db.HistoryEnvironment.Require(kind == "time", kind == "text");
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
            if (state.Ready)
            {
                var status = await StatusAsync(kind, token);
                if (status.Ready) { await transaction.CommitAsync(token); return true; }
                state.Ready = false;
                state.ActiveVersion = null;
            }
            var version = Version(kind);
            var complete = false;
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
            state.Examined += rows.Count;
            state.Prepared += rows.Count;
            if (state.Scanning && rows.Count > 0) state.LastId = rows[^1].Id;
            if (rows.Count == 0)
            {
                if (state.Scanning) state.Scanning = false;
                else
                {
                    if (!await IndexesReadyAsync(token)) throw new InvalidOperationException("Required history indexes are unavailable.");
                    state.Ready = activate;
                    state.ActiveVersion = activate ? version : null;
                    complete = true;
                }
            }
            state.LastFailure = null;
            state.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            db.ChangeTracker.Clear();
            return complete;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(kind, exception);
            throw;
        }
    }

    /// <summary>Retain committed checkpoints but revoke readiness on failure; an unavailable failure write cannot hide the primary error.</summary>
    private async Task RecordFailureAsync(string kind, Exception exception)
    {
        db.ChangeTracker.Clear();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.HistoryPreparationStates.Where(row => row.Kind == kind).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LastFailure, exception is OperationCanceledException ? "cancelled: resume prepare" : "preparation failed: verify database, indexes and rules; resume prepare")
                .SetProperty(row => row.Ready, false), deadline.Token);
        }
        catch (Exception) { /* Preserve the primary failure; previously committed progress remains authoritative. */ }
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
            && row.HistoryTimeStatus == "unresolved" && (!scanning || row.Id <= lastId), token) : 0;
        var indexes = await IndexesReadyAsync(token);
        var qualified = kind == "time" ? db.HistoryEnvironment.TimeQualified : db.HistoryEnvironment.TextQualified;
        var ready = qualified && state?.Ready == true && state.ActiveVersion == version && state.TargetVersion == version
            && state.WriterVersion == WriterVersion && pending == 0 && indexes;
        return new(kind, version, state?.ActiveVersion, ready, all, pending, all - pending - unresolved, unresolved, state?.Examined ?? 0,
            state?.Prepared ?? 0, state?.LastId ?? 0, indexes, state?.LastFailure, qualified, db.HistoryEnvironment.Diagnostics, qualified ? null : "historyQualificationRequired: use a qualified build/runtime/globalization profile; rebuilding cannot qualify it.");
    }

    /// <summary>
    /// Checks only collection prerequisites in the caller's snapshot. A single all-owner pending probe covers
    /// both required generations; operator totals and terminal unresolved counts are not needed to admit a read.
    /// </summary>
    internal async Task<bool> IsReadyAsync(bool time, bool text, CancellationToken token)
    {
        db.HistoryEnvironment.Require(time, text);
        if (!time && !text) return true;
        var states = await db.HistoryPreparationStates.AsNoTracking()
            .Where(state => time && state.Kind == "time" || text && state.Kind == "text").ToListAsync(token);
        foreach (var kind in new[] { "time", "text" })
        {
            if (kind == "time" ? !time : !text) continue;
            var state = states.SingleOrDefault(item => item.Kind == kind);
            if (state?.Ready != true || state.ActiveVersion != Version(kind) || state.TargetVersion != Version(kind)
                || state.WriterVersion != WriterVersion) return false;
        }
        if (!await IndexesReadyAsync(token)) return false;
        var timeState = states.SingleOrDefault(state => state.Kind == "time");
        var textState = states.SingleOrDefault(state => state.Kind == "text");
        var timeScanning = timeState?.Scanning ?? false; var timeLastId = timeState?.LastId ?? 0;
        var textScanning = textState?.Scanning ?? false; var textLastId = textState?.LastId ?? 0;
        return !await db.Locations.AnyAsync(row =>
            time && (row.HistoryTimeVersion == null || row.HistoryTimeVersion != LocationHistoryDerivation.TimeVersion
                || timeScanning && row.Id > timeLastId)
            || text && (row.HistoryTextVersion == null || row.HistoryTextVersion != LocationHistoryDerivation.TextVersion
                || textScanning && row.Id > textLastId), token);
    }

    /// <summary>Checks exact valid/ready indexes; both UTC payloads support preparation, while migrations own status coverage.</summary>
    internal Task<bool> IndexesReadyAsync(CancellationToken token) => db.Database.SqlQueryRaw<bool>("""
        SELECT EXISTS (SELECT FROM pg_extension WHERE extname = 'pg_trgm')
            AND EXISTS (SELECT FROM pg_attribute a JOIN pg_collation col ON col.oid = a.attcollation
                WHERE a.attrelid = '"Locations"'::regclass AND a.attname = 'HistorySearchText' AND col.collname = 'C') AND
            (SELECT count(*) = 4 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
             JOIN pg_am am ON am.oid = c.relam
             JOIN (VALUES
               ('IX_Location_HistoryUtc', 'btree', 3, 6, '"UserId", "LocalTimestamp", "Id", "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId"'),
               ('IX_Location_HistoryUtc', 'btree', 3, 7, '"UserId", "LocalTimestamp", "Id", "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId", "HistoryTimeStatus"'),
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
    long Total, long Pending, long Resolved, long Unresolved, long Examined, long Prepared, int LastId, bool IndexesReady, string? LastFailure, bool Qualified, object Environment, string? QualificationFailure);
