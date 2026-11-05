using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Concrete private calendar adapters. Caller owns admission, readiness, read-only snapshot and deadline.</summary>
internal sealed class TimelineCalendarReadService(ApplicationDbContext db)
{
    /// <summary>One admitted period read sequences markers, statistics and navigation without a page-owned request burst.</summary>
    internal async Task<object> PeriodAsync(string owner, TimelineCalendarScope scope, DateTime asOf, CancellationToken token)
    {
        var predicate = new HistoryPredicate(owner, scope.Query, token);
        var counts = await CountsAsync(predicate, token);
        var markers = await MarkersAsync(predicate, scope, counts.Known, token);
        var summary = await new LocationStatsService(db).GetCalendarSummaryAsync(predicate, token);
        var endpoints = await TimelineStatistics.EndpointsAsync(db, predicate, scope, false, token);
        var result = Envelope(owner, scope, asOf);
        result["counts"] = Counts(counts); result["markers"] = markers;
        result["markersSampled"] = scope.DateType != "day" && counts.Known > 3000;
        result["statistics"] = summary; result["endpoints"] = endpoints;
        result["existence"] = counts.Known > 0 ? "present" : counts.Candidates > 0 ? "unknown" : "absent";
        result["navigation"] = scope.Navigation(Instant.FromDateTimeUtc(asOf));
        return result;
    }

    /// <summary>Deliberate geographic drill-down keeps the same membership and actual endpoint clocks.</summary>
    internal async Task<object> DetailsAsync(string owner, TimelineCalendarScope scope, DateTime asOf, CancellationToken token)
    {
        var predicate = new HistoryPredicate(owner, scope.Query, token);
        var counts = await CountsAsync(predicate, token);
        var stats = await new LocationStatsService(db).GetCalendarDetailsAsync(predicate, token);
        var endpoints = await TimelineStatistics.EndpointsAsync(db, predicate, scope, true, token);
        var result = Envelope(owner, scope, asOf);
        result["counts"] = Counts(counts); result["statistics"] = stats; result["endpoints"] = endpoints;
        return result;
    }

    /// <summary>Every finite event in the envelope is streamed directly through Npgsql, bypassing EF retry buffering.</summary>
    internal async Task<object> AnalysisAsync(string owner, TimelineCalendarScope scope, TimelineConnectionRules rules,
        bool geometry, DateTime asOf, CancellationToken token)
    {
        var predicate = new HistoryPredicate(owner, scope.Query, token);
        var invalid = await db.Database.SqlQueryRaw<bool>($"""
            SELECT EXISTS (SELECT FROM "Locations" l WHERE {predicate.Where} AND NOT ({HistoryPredicate.EventValid})) AS "Value"
            """, predicate.CopyParameters()).SingleAsync(token);
        var reducer = new TimelineConnections(rules, geometry && !invalid);
        await using (var command = Command(SourceSql(predicate), predicate))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
        {
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                reducer.Add(new(reader.GetInt32(0), reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4)));
            }
        }
        var analysis = reducer.Finish(invalid);
        var result = Envelope(owner, scope, asOf);
        result["contract"] = "stored-history-connections/1";
        result["source"] = "effective-stored-owner-history"; result["unplaceableEventExists"] = invalid;
        result["rules"] = new { maximumGapSeconds = rules.MaximumGapSeconds,
            speedEnabled = rules.MaximumSpeedMetresPerSecond != null, maximumSpeedMetresPerSecond = rules.MaximumSpeedMetresPerSecond,
            radiusMetres = TimelineConnections.RadiusMetres, thresholdEquality = "included" };
        result["coverage"] = analysis.Coverage; result["distanceMetres"] = analysis.DistanceMetres;
        result["counts"] = new { examinedSourceRows = Count(analysis.SourceRowCount), knownMatches = Count(analysis.KnownMatchCount),
            mappedMatches = Count(analysis.MappedMatchCount), unmappedMatches = Count(analysis.KnownMatchCount - analysis.MappedMatchCount),
            unresolvedPlaceableCandidates = Count(analysis.UnresolvedPlaceableCandidateCount), eligibleEdges = Count(analysis.EdgeCount),
            equalTimeDisagreementGroups = Count(analysis.EqualTimeDisagreementGroups), gapBreaks = Count(analysis.GapBreaks), speedBreaks = Count(analysis.SpeedBreaks) };
        result["geometry"] = new { state = geometry && invalid ? "unavailable" : analysis.GeometryState,
            reason = geometry && (invalid || analysis.GeometryState == "unavailable") ? invalid ? "unplaceableEventTime" : "geometryBudgetExceeded" : null,
            islands = analysis.Geometry, maximumVertices = TimelinePathGeometry.MaximumVertices,
            maximumIslands = TimelinePathGeometry.MaximumIslands, maximumBytes = TimelinePathGeometry.MaximumBytes };
        return result;
    }

    /// <summary>No Matches filter, paging, tie cap or capture-local index narrowing may enter this continuity source.</summary>
    internal static string SourceSql(HistoryPredicate predicate) => $"""
        SELECT l."Id",l."LocalTimestamp",{predicate.Truth},
            CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Longitude} END,
            CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Latitude} END
        FROM "Locations" l WHERE {predicate.Where} AND ({HistoryPredicate.EventValid}) AND {predicate.EventEnvelope}
        ORDER BY l."LocalTimestamp",l."Id"
        """;

    /// <summary>Exact three-valued membership counts include unplaceable candidates independently of mapped records.</summary>
    private Task<TimelineCalendarCounts> CountsAsync(HistoryPredicate predicate, CancellationToken token) =>
        db.Database.SqlQueryRaw<TimelineCalendarCounts>($"""
            SELECT count(*) FILTER (WHERE ({predicate.Truth}) IS TRUE) AS "Known",
                count(*) FILTER (WHERE ({predicate.Truth}) IS TRUE AND ({HistoryPredicate.PositionValid})) AS "Mapped",
                count(*) FILTER (WHERE ({predicate.Truth}) IS NULL) AS "Candidates"
            FROM "Locations" l WHERE {predicate.Where}
            """, predicate.CopyParameters()).SingleAsync(token);

    /// <summary>Display sampling follows membership/position validity; rich day records remain intentionally unsampled.</summary>
    private async Task<object[]> MarkersAsync(HistoryPredicate predicate, TimelineCalendarScope scope, long known, CancellationToken token)
    {
        var selected = $"SELECT l.\"Id\",l.\"LocalTimestamp\" FROM \"Locations\" l WHERE {predicate.Where} AND {predicate.Matches} AND ({HistoryPredicate.PositionValid})";
        if (scope.DateType != "day" && known > 3000)
            selected = $"""
                SELECT "Id","LocalTimestamp" FROM (
                    SELECT l."Id",l."LocalTimestamp",row_number() OVER (
                        PARTITION BY ST_GeoHash(l."Coordinates"::geometry,5) ORDER BY l."LocalTimestamp" DESC,l."Id" DESC) AS rank
                    FROM "Locations" l WHERE {predicate.Where} AND {predicate.Matches} AND ({HistoryPredicate.PositionValid})
                ) ranked WHERE rank <= 5 ORDER BY "LocalTimestamp" DESC,"Id" DESC LIMIT 3000
                """;
        var sql = $"""
            SELECT {string.Join(",", HistoryDetail.Fields())}
            FROM "Locations" l JOIN ({selected}) selected ON selected."Id" = l."Id"
            LEFT JOIN "ActivityTypes" a ON a."Id" = l."ActivityTypeId" ORDER BY l."LocalTimestamp",l."Id"
            """;
        await using var command = Command(sql, predicate);
        await using var reader = await command.ExecuteReaderAsync(token);
        var markers = new List<object>();
        while (await reader.ReadAsync(token))
        {
            token.ThrowIfCancellationRequested();
            var (record, unavailable) = HistoryDetail.Record(reader, scope.Query.Zone, db.HistoryEnvironment, token);
            markers.Add(new { record, unavailableFields = unavailable });
        }
        return markers.ToArray();
    }

    /// <summary>Raw readers are tied to the existing transaction and never enable implicit retries or tracking.</summary>
    private NpgsqlCommand Command(string sql, HistoryPredicate predicate)
    {
        var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.Transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.AddRange(predicate.CopyParameters().Cast<NpgsqlParameter>().ToArray());
        return command;
    }

    /// <summary>Each independent operation discloses one response snapshot and the same canonical scope/rules authority.</summary>
    private Dictionary<string, object?> Envelope(string owner, TimelineCalendarScope scope, DateTime asOf) => new()
    {
        ["contract"] = "private-calendar/1", ["subjectId"] = owner, ["selection"] = scope.Selection, ["scope"] = scope.Query.Scope,
        ["timeRules"] = new { version = LocationHistoryDerivation.TimeVersion, tzdb = db.HistoryEnvironment.Tzdb },
        ["observationId"] = Guid.NewGuid().ToString("D"), ["asOfUtc"] = HistoryQuery.Utc(asOf),
        ["consistency"] = "response-snapshot", ["freshness"] = "current-at-observation"
    };

    private static object Counts(TimelineCalendarCounts counts) => new { knownMatches = Count(counts.Known),
        mappedMatches = Count(counts.Mapped), unmappedMatches = Count(counts.Known - counts.Mapped), unresolvedCandidates = Count(counts.Candidates) };
    private static string Count(long count) => count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Exact database counts retain signed-64-bit precision before decimal-string publication.</summary>
internal sealed class TimelineCalendarCounts
{
    public long Known { get; set; }
    public long Mapped { get; set; }
    public long Candidates { get; set; }
}
