using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Reads only inside the route's short read-only repeatable-read transaction.
/// Interpolated SQL contains compiler-owned structure only; every request/anchor value is a bound parameter.
/// </summary>
internal sealed class HistoryReadService(ApplicationDbContext db, HistoryCursor cursors)
{
    /// <summary>All-owner readiness is checked in the response snapshot; no read repairs or weakens preparation.</summary>
    internal async Task PrepareQueryAsync(string owner, HistoryQuery query, CancellationToken token)
    {
        if (query.Scope.ActivityIds is { Length: > 0 } ids)
        {
            var canonical = await new ActivityCatalog(db).ResolveIdsAsync(ids, token) ?? throw new HistoryRequestException("activityInvalid");
            query.Scope = query.Scope with { ActivityIds = canonical };
        }
        cursors.Read(owner, query, HistoryCursor.ScopeKey(owner, query.Scope));
        var preparation = new LocationHistoryPreparation(db);
        if (!await preparation.IsReadyAsync(query.Scope.TimeBasis == "capture", query.Scope.Q.Length != 0, token))
            throw new HistoryRequestException("historyPreparationRequired", 503);
    }

    /// <summary>Independent candidate counts cover the whole scope, before either result set's continuation.</summary>
    private async Task<long> CandidateCountAsync(HistoryPredicate predicate, CancellationToken token)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM \"Locations\" l WHERE {predicate.Where} AND {predicate.Truth} IS NULL";
        return await db.Database.SqlQueryRaw<long>(sql, predicate.CopyParameters()).SingleAsync(token);
    }

    /// <summary>Unsampled pages use strict value anchors; deletion or movement of the anchor needs no lookup.</summary>
    internal async Task<object> PageAsync(string owner, HistoryQuery query, CancellationToken token)
    {
        var key = HistoryCursor.ScopeKey(owner, query.Scope);
        var anchor = cursors.Read(owner, query, key);
        var predicate = new HistoryPredicate(owner, query, token);
        var candidates = await CandidateCountAsync(predicate, token);
        var candidate = query.ResultSet == "unresolvedCandidates";
        var tail = "";
        if (anchor != null)
            tail = candidate ? $"AND l.\"Id\" < {predicate.Add(anchor.Id)}"
                : $"AND (l.\"LocalTimestamp\",l.\"Id\") < ({predicate.Add(anchor.EventUtc!.Value)},{predicate.Add(anchor.Id)})";
        var sql = $"""
            SELECT l."Id", l."IdempotencyKey" AS "Uuid",
                CASE WHEN {HistoryPredicate.EventValid} THEN l."LocalTimestamp" END AS "EventUtc",
                CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Longitude} END AS "Longitude",
                CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Latitude} END AS "Latitude",
                l."CaptureLocalTimestamp" AS "CaptureLocal", l."CaptureTimeZoneId" AS "CaptureZone", l."CaptureOffsetSeconds" AS "CaptureOffset",
                l."HistoryTimeStatus" AS "TimeStatus", l."HistoryTimeVersion" AS "TimeVersion", l."HistoryTimeFailureCode" AS "TimeFailure",
                l."ActivityTypeId", left(a."Name",161) AS "ActivityName",
                left(coalesce(nullif(l."ResolvedFeatureName",''),nullif(l."FullAddress",''),nullif(l."Address",''),l."Place"),161) AS "Label"
            FROM "Locations" l LEFT JOIN "ActivityTypes" a ON a."Id" = l."ActivityTypeId"
            WHERE {predicate.Where} AND {(candidate ? predicate.Truth + " IS NULL" : predicate.Matches)} {tail}
            ORDER BY {(candidate ? "l.\"Id\" DESC" : "l.\"LocalTimestamp\" DESC, l.\"Id\" DESC")} LIMIT {predicate.Add(query.PageSize + 1)}
            """;
        var rows = await db.Database.SqlQueryRaw<HistorySummaryRow>(sql, predicate.CopyParameters()).ToListAsync(token);
        string? continuation = null;
        if (rows.Count > query.PageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            continuation = cursors.Protect(owner, query, key, rows[^1].Id, rows[^1].EventUtc);
        }
        var result = Envelope(owner, query, key, HistoryCursor.Profile(query), candidates, candidate ? "candidatesPage" : "page");
        var items = new List<object>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            items.Add(HistoryPublication.Summary(row, query.Zone, candidate));
        }
        result["items"] = items;
        result["nextContinuation"] = continuation;
        return result;
    }

    /// <summary>
    /// Groups every definite coordinate in PostgreSQL; geography is independent of chronology traversal.
    /// Streaming OFFSET 0 boundaries reuse numeric extraction and range validation before edge comparisons
    /// during planning/JIT. They neither limit matches nor materialize an all-match temporary store.
    /// </summary>
    internal async Task<object> OverviewAsync(string owner, HistoryQuery query, CancellationToken token)
    {
        var key = HistoryCursor.ScopeKey(owner, query.Scope);
        var predicate = new HistoryPredicate(owner, query, token);
        var candidates = await CandidateCountAsync(predicate, token);
        var area = query.Scope.Area ?? new HistoryArea(-180, -90, 180, 90);
        var longitudes = PartitionEdges(area.West, area.East, area.Span, query.Grid, true);
        var latitudes = PartitionEdges(area.South, area.North, area.North - area.South, query.Grid, false);
        var x = CellIndex(predicate, longitudes, true); var y = CellIndex(predicate, latitudes, false);
        var west = predicate.Add(area.West);
        // Geography storage is point-typed; retain the empty/SRID guard before extracting either ordinate.
        var point = "CASE WHEN NOT ST_IsEmpty(l.\"Coordinates\"::geometry) AND ST_SRID(l.\"Coordinates\"::geometry) = 4326 THEN l.\"Coordinates\"::geometry END";
        const string valid = "raw_lon BETWEEN -180 AND 180 AND raw_lat BETWEEN -90 AND 90";
        var sql = $"""
            WITH matches AS (
                SELECT l."Id", l."IdempotencyKey", l."LocalTimestamp",
                    ST_X({point}) AS raw_lon, ST_Y({point}) AS raw_lat
                FROM "Locations" l WHERE {predicate.Where} AND {predicate.Matches}
                OFFSET 0
            ), positions AS (
                SELECT "Id", "IdempotencyKey", "LocalTimestamp",
                    CASE WHEN {valid} THEN CASE WHEN raw_lon = 180 THEN -180.0 ELSE raw_lon END END AS lon,
                    CASE WHEN {valid} THEN raw_lat END AS lat
                FROM matches OFFSET 0
            ), unwrapped AS (
                SELECT *, CASE WHEN lon < {west} THEN lon + 360 ELSE lon END AS ux FROM positions
            ), cells AS (
                SELECT *, {x} AS x, {y} AS y FROM unwrapped
            )
            SELECT x AS "X", y AS "Y", count(*) AS "Count", min(ux) AS "West", max(ux) AS "East",
                min(lat) AS "South", max(lat) AS "North", min("LocalTimestamp") AS "Earliest", max("LocalTimestamp") AS "Latest",
                CASE WHEN count(*) = 1 THEN min("Id") END AS "SingletonId",
                CASE WHEN count(*) = 1 THEN min("IdempotencyKey"::text) END AS "SingletonUuid"
            FROM cells GROUP BY x,y ORDER BY x NULLS LAST,y NULLS LAST
            """;
        var rows = await db.Database.SqlQueryRaw<HistoryCellRow>(sql, predicate.CopyParameters()).ToListAsync(token);
        var cells = new List<object>();
        long mapped = 0, unmapped = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.X == null) { unmapped += row.Count; continue; }
            mapped += row.Count;
            cells.Add(new
            {
                cellKey = HistoryCursor.Hash($"{key}:{query.Grid}:{row.X}:{row.Y}"),
                bounds = Bounds(longitudes[row.X.Value], latitudes[row.Y!.Value],
                    longitudes[row.X.Value + 1], latitudes[row.Y.Value + 1], true),
                occupiedBounds = Bounds(row.West!.Value, row.South!.Value, row.East!.Value, row.North!.Value, false),
                displayPoint = new { longitude = Canonical((row.West.Value + row.East.Value) / 2), latitude = (row.South.Value + row.North.Value) / 2 },
                recordCount = Count(row.Count), earliestEventUtc = HistoryQuery.Utc(row.Earliest), latestEventUtc = HistoryQuery.Utc(row.Latest),
                singleton = row.Count == 1 ? new { locationId = row.SingletonId, uuid = row.SingletonUuid } : null
            });
        }
        var result = Envelope(owner, query, key, "owner-overview-1", candidates, "aggregation");
        result["grid"] = query.Grid; result["knownMatchCount"] = Count(mapped + unmapped);
        result["mappedMatchCount"] = Count(mapped); result["unmappedMatchCount"] = Count(unmapped); result["cells"] = cells;
        return result;
    }

    /// <summary>Compute representable edges once, preserving parent endpoints exactly, before binding or publishing them.</summary>
    private static double[] PartitionEdges(double start, double end, double span, int grid, bool longitude)
    {
        var edges = new double[grid + 1];
        edges[0] = start; edges[grid] = end;
        for (var i = 1; i < grid; i++)
        {
            var edge = start + i * span / grid;
            edges[i] = longitude && edge != 180 ? Canonical(edge) : edge;
        }
        return edges;
    }

    /// <summary>
    /// Compare canonical stored coordinates with the exact wire edges, using the scope's half-open residuals.
    /// Unwrapping is only for occupied extents: adding 360 can round distinct coordinates onto the same edge.
    /// Sub-ULP partitions may have coincident edges; their empty intervals own no records.
    /// </summary>
    private static string CellIndex(HistoryPredicate predicate, double[] edges, bool longitude)
    {
        var cases = new List<string>();
        for (var i = 0; i < edges.Length - 1; i++)
        {
            if (edges[i] == edges[i + 1]) continue;
            var lower = longitude ? Canonical(edges[i]) : edges[i];
            var upper = edges[i + 1];
            if (lower == upper) continue;
            var lo = predicate.Add(lower); var hi = predicate.Add(upper);
            var contains = longitude ? HistoryPredicate.LongitudeWithin("lon", lo, hi, lower > upper)
                : HistoryPredicate.LatitudeWithin("lat", lo, hi);
            cases.Add($"WHEN {contains} THEN {i}");
        }
        return "CASE " + string.Join(" ", cases) + " END";
    }

    /// <summary>Common metadata makes live scope and conservative membership explicit on both collection operations.</summary>
    private static Dictionary<string, object?> Envelope(string owner, HistoryQuery query, string key, string profile, long candidates, string extent) => new()
    {
        ["subjectId"] = owner, ["profile"] = profile, ["scopeKey"] = key, ["scope"] = query.Scope,
        ["derivationVersions"] = HistoryPublication.Versions, ["observedAtUtc"] = HistoryQuery.Utc(DateTime.UtcNow),
        ["consistency"] = "live-request", ["coverage"] = new { membership = candidates == 0 ? "complete" : "incomplete",
            resultExtent = extent, unresolvedCandidateCount = Count(candidates) }
    };

    /// <summary>Counts use decimal strings so clients cannot lose signed-64-bit precision.</summary>
    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);
    /// <summary>Aggregation uses an unwrapped span internally; external longitudes stay canonical.</summary>
    private static double Canonical(double longitude) => longitude >= 180 ? longitude - 360 : longitude;
    /// <summary>Preserve +180 only as a rectangle end; occupied bounds may have coincident edges.</summary>
    private static object Bounds(double west, double south, double east, double north, bool rectangle) => new
    { west = Canonical(west), south, east = rectangle && east == 180 ? 180 : Canonical(east), north };
}

/// <summary>Only compact grouped facts leave PostgreSQL, including a separate null-position bucket.</summary>
internal sealed class HistoryCellRow
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public long Count { get; set; }
    public double? West { get; set; }
    public double? East { get; set; }
    public double? South { get; set; }
    public double? North { get; set; }
    public DateTime Earliest { get; set; }
    public DateTime Latest { get; set; }
    public int? SingletonId { get; set; }
    public string? SingletonUuid { get; set; }
}
