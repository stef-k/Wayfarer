using System.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Parameterized, three-valued SQL: true matches, null candidates, false reliable exclusions.</summary>
internal sealed class HistoryPredicate
{
    internal const string EventValid = "isfinite(l.\"LocalTimestamp\") AND l.\"LocalTimestamp\" >= timestamptz '0001-01-01 00:00:00+00' AND l.\"LocalTimestamp\" < timestamptz '10000-01-01 00:00:00+00'";
    internal const string PositionValid = "NOT ST_IsEmpty(l.\"Coordinates\"::geometry) AND ST_SRID(l.\"Coordinates\"::geometry) = 4326 AND ST_X(l.\"Coordinates\"::geometry) BETWEEN -180 AND 180 AND ST_Y(l.\"Coordinates\"::geometry) BETWEEN -90 AND 90";
    internal const string Longitude = "CASE WHEN ST_X(l.\"Coordinates\"::geometry) = 180 THEN -180.0 ELSE ST_X(l.\"Coordinates\"::geometry) END";
    internal const string Latitude = "ST_Y(l.\"Coordinates\"::geometry)";
    internal readonly List<NpgsqlParameter> Parameters = [];
    internal string Where { get; }
    internal string Truth { get; }

    /// <summary>All reliable dimensions precede page/aggregation; unknown event/area evidence cannot disappear.</summary>
    internal HistoryPredicate(string owner, HistoryQuery query, CancellationToken token)
    {
        var scope = query.Scope;
        var filters = new List<string> { $"l.\"UserId\" = {Add(owner)}" };
        if (scope.ActivityIds != null)
        {
            var ids = Add(scope.ActivityIds);
            filters.Add($"(l.\"ActivityTypeId\" = ANY({ids}){(scope.IncludeUnassigned ? " OR l.\"ActivityTypeId\" IS NULL" : "")})");
        }
        if (scope.Q.Length != 0)
        {
            // pg_trgm extracts literal alphanumeric runs, while short/punctuation searches use exact strpos.
            var usefulTrigrams = System.Text.RegularExpressions.Regex.IsMatch(scope.Q, @"[\p{L}\p{N}]{3}");
            filters.Add(usefulTrigrams
                ? $"l.\"HistorySearchText\" LIKE {Add("%" + scope.Q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%")} ESCAPE E'\\\\'"
                : $"strpos(l.\"HistorySearchText\", {Add(scope.Q)}) > 0");
        }
        var time = TimeTruth(query, token);
        var area = scope.Area == null ? "TRUE" : AreaTruth(scope.Area);
        Where = string.Join(" AND ", filters.Select(value => "(" + value + ")"));
        Truth = $"(({time}) AND ({area}))";
    }

    /// <summary>A fresh parameter instance is required for each EF/Npgsql command.</summary>
    internal object[] CopyParameters() => Parameters.Select(parameter => (object)((ICloneable)parameter).Clone()).ToArray();
    internal string Add(object value)
    {
        var name = "p" + Parameters.Count;
        Parameters.Add(new NpgsqlParameter(name, value));
        return "@" + name;
    }

    /// <summary>Persisted capture values are final predicates; NodaTime segments supply viewer offsets in SQL.</summary>
    private string TimeTruth(HistoryQuery query, CancellationToken token)
    {
        if (query.Scope.TimeBasis == "utc")
            return $"CASE WHEN {EventValid} THEN l.\"LocalTimestamp\" >= {Add(query.Start)} AND l.\"LocalTimestamp\" < {Add(query.End)} END";
        var start = query.Scope.TimeMode == "daily" ? query.Start.Add(query.FromTime) : query.Start;
        var end = query.Scope.TimeMode == "daily"
            ? (query.Overnight ? query.End : query.End.AddDays(-1)).Add(query.UntilTime) : query.End;
        var min = query.Scope.TimeBasis == "viewer" ? query.Zone.MinOffset.Seconds : DateTimeZoneProviders.Tzdb.Ids.Min(id => DateTimeZoneProviders.Tzdb[id].MinOffset.Seconds);
        var max = query.Scope.TimeBasis == "viewer" ? query.Zone.MaxOffset.Seconds : DateTimeZoneProviders.Tzdb.Ids.Max(id => DateTimeZoneProviders.Tzdb[id].MaxOffset.Seconds);
        var lower = ClipUtc(start, -max);
        var upper = ClipUtc(end, -min);
        var envelope = $"l.\"LocalTimestamp\" >= {Add(lower)} AND l.\"LocalTimestamp\" <= {Add(upper)}";
        if (query.Scope.TimeBasis == "capture")
        {
            var wall = Wall("l.\"CaptureLocalTimestamp\"", query);
            return $"CASE WHEN NOT ({EventValid}) THEN NULL WHEN NOT ({envelope}) THEN FALSE WHEN l.\"HistoryTimeStatus\" = 'resolved' THEN {wall} END";
        }
        var starts = new List<DateTime>(); var ends = new List<DateTime>(); var offsets = new List<int>();
        var first = Instant.FromDateTimeUtc(lower); var last = Instant.FromDateTimeUtc(upper);
        foreach (var interval in query.Zone.GetZoneIntervals(first, last))
        {
            token.ThrowIfCancellationRequested();
            starts.Add((interval.HasStart && interval.Start > first ? interval.Start : first).ToDateTimeUtc());
            ends.Add((interval.HasEnd && interval.End < last ? interval.End : last).ToDateTimeUtc());
            offsets.Add(interval.WallOffset.Seconds);
        }
        // The clipped maximum is inclusive at microsecond precision; add one tick-free microsecond in SQL below.
        var lo = Add(starts.ToArray()); var hi = Add(ends.ToArray()); var off = Add(offsets.ToArray());
        var local = "((l.\"LocalTimestamp\" AT TIME ZONE 'UTC') + s.off * interval '1 second')";
        var predicate = Wall(local, query);
        return $"CASE WHEN NOT ({EventValid}) THEN NULL WHEN NOT ({envelope}) THEN FALSE ELSE EXISTS (SELECT FROM unnest({lo}::timestamptz[], {hi}::timestamptz[], {off}::integer[]) s(lo,hi,off) WHERE l.\"LocalTimestamp\" >= s.lo AND l.\"LocalTimestamp\" < s.hi AND {predicate}) END";
    }

    /// <summary>Wall arithmetic preserves fold occurrences, gaps, non-hour offsets and overnight anchor dates.</summary>
    private string Wall(string local, HistoryQuery query)
    {
        var start = Add(query.Start); var end = Add(query.End);
        if (query.Scope.TimeMode != "daily") return $"{local} >= {start} AND {local} < {end}";
        var from = Add(query.FromTime); var until = Add(query.UntilTime);
        var date = query.Overnight ? $"({local}::date - CASE WHEN {local}::time < {until} THEN 1 ELSE 0 END)" : $"{local}::date";
        var hours = query.Overnight ? $"({local}::time >= {from} OR {local}::time < {until})" : $"({local}::time >= {from} AND {local}::time < {until})";
        return $"{date} >= {start} AND {date} < {end} AND {hours}";
    }

    /// <summary>GiST boxes prune valid coordinates; residuals own half-open and +180 alias membership.</summary>
    private string AreaTruth(HistoryArea area)
    {
        var west = Add(area.West); var east = Add(area.East); var south = Add(area.South); var north = Add(area.North);
        var longitude = area.West < area.East ? $"{Longitude} >= {west} AND {Longitude} < {east}" : $"({Longitude} >= {west} OR {Longitude} < {east})";
        var geometry = "l.\"Coordinates\"::geometry";
        var box = area.West < area.East ? $"{geometry} && ST_MakeEnvelope({west},{south},{east},{north},4326)"
            : $"({geometry} && ST_MakeEnvelope({west},{south},180,{north},4326) OR {geometry} && ST_MakeEnvelope(-180,{south},{east},{north},4326))";
        // Longitude +180 is canonical -180, even for a non-wrapping rectangle beginning at -180.
        if (area.West == -180 || area.West > area.East) box = $"({box} OR ({geometry} && ST_MakeEnvelope(180,{south},180,{north},4326)))";
        return $"CASE WHEN {PositionValid} THEN {box} AND ({longitude}) AND {Latitude} >= {south} AND ({Latitude} < {north} OR ({north} = 90 AND {Latitude} = 90)) END";
    }

    /// <summary>Optimizing envelopes clip safely; the exact predicate always makes the membership decision.</summary>
    private static DateTime ClipUtc(DateTime local, int seconds)
    {
        var ticks = Math.Clamp(local.Ticks + seconds * TimeSpan.TicksPerSecond, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks - 9);
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
