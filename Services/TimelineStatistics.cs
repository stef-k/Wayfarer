using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>Qualified endpoint facts for the existing geographic statistics, independent of representative coordinates.</summary>
internal static class TimelineStatistics
{
    /// <summary>Read exact first/last by event/ID for each normalized hierarchy key in the same response snapshot.</summary>
    internal static async Task<object[]> EndpointsAsync(ApplicationDbContext db, HistoryPredicate predicate,
        TimelineCalendarScope scope, bool detailed, CancellationToken token)
    {
        var kinds = detailed ? "('period'),('country'),('region'),('city')" : "('period')";
        var sql = LocationStatsService.CalendarScope(predicate) + $"""
            , keyed AS (
                SELECT s."Id", s."VisitTime", k.kind,
                    CASE WHEN k.kind <> 'period' THEN s."Country" ELSE '' END AS country,
                    CASE WHEN k.kind IN ('region','city') THEN s."Region" ELSE '' END AS region,
                    CASE WHEN k.kind = 'city' THEN s."Place" ELSE '' END AS place
                FROM scoped s CROSS JOIN (VALUES {kinds}) k(kind)
                WHERE k.kind = 'period' OR k.kind = 'country' AND s."Country" <> ''
                    OR k.kind = 'region' AND s."Region" <> '' OR k.kind = 'city' AND s."Place" <> ''
            ), ranked AS (
                SELECT *, row_number() OVER (PARTITION BY kind,country,region,place ORDER BY "VisitTime","Id") AS first,
                    row_number() OVER (PARTITION BY kind,country,region,place ORDER BY "VisitTime" DESC,"Id" DESC) AS last
                FROM keyed
            )
            SELECT r.kind AS "Kind", r.country AS "Country", r.region AS "Region", r.place AS "Place",
                (r.first = 1) AS "First", (r.last = 1) AS "Last", l."Id", l."LocalTimestamp" AS "EventUtc",
                l."CaptureLocalTimestamp" AS "CaptureLocal", l."CaptureTimeZoneId" AS "CaptureZone",
                l."CaptureOffsetSeconds" AS "CaptureOffset", l."HistoryTimeStatus" AS "TimeStatus",
                l."HistoryTimeVersion" AS "TimeVersion", l."HistoryTimeFailureCode" AS "TimeFailure"
            FROM ranked r JOIN "Locations" l ON l."Id" = r."Id"
            WHERE r.first = 1 OR r.last = 1 ORDER BY r.kind,r.country,r.region,r.place,l."LocalTimestamp",l."Id"
            """;
        var rows = await db.Database.SqlQueryRaw<TimelineStatisticsEndpoint>(sql, predicate.CopyParameters()).ToListAsync(token);
        return rows.Select(row => (object)new
        {
            kind = row.Kind, country = row.Country, region = row.Region, place = row.Place, first = row.First, last = row.Last,
            locationId = row.Id, eventUtc = HistoryQuery.Utc(row.EventUtc),
            captureTime = HistoryPublication.Capture(row.CaptureLocal, row.CaptureZone, row.CaptureOffset, row.TimeStatus,
                row.TimeVersion, row.TimeFailure, db.HistoryEnvironment),
            viewerTime = HistoryPublication.Viewer(row.EventUtc, scope.Query.Zone, db.HistoryEnvironment)
        }).ToArray();
    }
}

/// <summary>Only actual endpoint facts cross SQL; averages never participate in capture-time publication.</summary>
internal sealed class TimelineStatisticsEndpoint
{
    public string Kind { get; set; } = "";
    public string Country { get; set; } = "";
    public string Region { get; set; } = "";
    public string Place { get; set; } = "";
    public bool First { get; set; }
    public bool Last { get; set; }
    public int Id { get; set; }
    public DateTime EventUtc { get; set; }
    public DateTime? CaptureLocal { get; set; }
    public string? CaptureZone { get; set; }
    public int? CaptureOffset { get; set; }
    public string? TimeStatus { get; set; }
    public string? TimeVersion { get; set; }
    public string? TimeFailure { get; set; }
}
