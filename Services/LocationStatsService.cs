using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Parsers;

public interface ILocationStatsService
{
    /// <summary>Summarizes the full eligible public history without viewport sampling.</summary>
    Task<UserLocationStatsDto> GetPublicStatsAsync(PublicTimelineLocationProjection projection);
    Task<UserLocationStatsDto> GetStatsForUserAsync(string userId);
    Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate);
    Task<UserLocationStatsDetailedDto> GetDetailedStatsForUserAsync(string userId);
    Task<UserLocationStatsDetailedDto> GetDetailedStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate);
}

/// <summary>
/// Calculates statistics about user location data
/// </summary>
public class LocationStatsService : ILocationStatsService
{
    private readonly ApplicationDbContext _db;

    public LocationStatsService(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>Reuses normalization and UTC summary dates over the authoritative public source.</summary>
    public Task<UserLocationStatsDto> GetPublicStatsAsync(PublicTimelineLocationProjection projection) =>
        ReadSummaryAsync(StatisticsScope(false, PublicTimelineLocationProjection.SourceSql + " AS eligible"), projection.Bind());

    /// <summary>Summarizes all records using their UTC Timestamp.</summary>
    public Task<UserLocationStatsDto> GetStatsForUserAsync(string userId) =>
        ReadSummaryAsync(StatisticsScope(false), [userId]);

    /// <summary>Summarizes the inclusive LocalTimestamp window.</summary>
    public Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate) =>
        ReadSummaryAsync(StatisticsScope(true), [userId, startDate, endDate]);

    /// <summary>Returns all-time groups, using Timestamp for visits and representatives.</summary>
    public Task<UserLocationStatsDetailedDto> GetDetailedStatsForUserAsync(string userId) =>
        ReadDetailsAsync(StatisticsScope(false), [userId]);

    /// <summary>Returns groups scoped inclusively by LocalTimestamp, also used for visit dates.</summary>
    public Task<UserLocationStatsDetailedDto> GetDetailedStatsForDateRangeAsync(
        string userId, DateTime startDate, DateTime endDate) =>
        ReadDetailsAsync(StatisticsScope(true), [userId, startDate, endDate]);

    /// <summary>Private explicit membership reuses the geographic aggregate owner and preserves legacy defaults.</summary>
    internal Task<UserLocationStatsDto> GetCalendarSummaryAsync(HistoryPredicate predicate, CancellationToken token) =>
        ReadSummaryAsync(CalendarScope(predicate), predicate.CopyParameters(), token);

    /// <summary>Detailed groups use the same selected source, cancellation and event-order clock.</summary>
    internal Task<UserLocationStatsDetailedDto> GetCalendarDetailsAsync(HistoryPredicate predicate, CancellationToken token) =>
        ReadDetailsAsync(CalendarScope(predicate), predicate.CopyParameters(), token);

    /// <summary>Invalid map positions remain matching records while contributing no synthetic coordinate.</summary>
    internal static string CalendarScope(HistoryPredicate predicate) => StatisticsScope(true, $"""
        (SELECT l."Id", l."LocalTimestamp", l."Country", l."Region", l."Place",
            CASE WHEN {HistoryPredicate.PositionValid} THEN l."Coordinates" END AS "Coordinates"
         FROM "Locations" l WHERE {predicate.Where} AND {predicate.Matches}) AS eligible
        """, true);

    /// <summary>
    /// Shared read-only projection for every statistics query. PostgreSQL C collation keeps
    /// component equality exact. btrim removes only the six authorized ASCII characters;
    /// empty strings represent missing components internally and in the legacy DTOs.
    /// SQL syntax comes only from the fixed timestamp/bounds choices, PublicTimelineLocationProjection.SourceSql,
    /// or CalendarScope's HistoryPredicate fragments. Owner, cutoff, text/activity, time/offset and area values
    /// remain EF placeholders or typed Npgsql parameters; source strings never come from callers outside this class.
    /// See issue #977 for the call-site analyzer assessment.
    /// </summary>
    private static string StatisticsScope(bool dateRange, string? publicSource = null, bool selected = false)
    {
        var timestamp = dateRange ? "LocalTimestamp" : "Timestamp";
        var bounds = dateRange && !selected ? "AND \"LocalTimestamp\" >= {1} AND \"LocalTimestamp\" <= {2}" : "";
        var source = publicSource ?? "\"Locations\"";
        var owner = publicSource is null ? "WHERE \"UserId\" = {0}" : "";
        return $$"""
            WITH trimmed AS (
                SELECT "Id", "Coordinates", "{{timestamp}}" AS "VisitTime",
                    btrim(COALESCE("Country", ''), E'\x20\x09\x0A\x0B\x0C\x0D') COLLATE "C" AS "Country",
                    btrim(COALESCE("Region", ''), E'\x20\x09\x0A\x0B\x0C\x0D') COLLATE "C" AS "Region",
                    btrim(COALESCE("Place", ''), E'\x20\x09\x0A\x0B\x0C\x0D') COLLATE "C" AS "Place"
                FROM {{source}} {{owner}} {{bounds}}
            ), scoped AS (
                SELECT "Id", "Coordinates", "VisitTime", "Country", "Place",
                    (CASE WHEN "Country" = 'Greece' AND "Region" = 'East Macedonia and Thrace'
                        THEN 'Eastern Macedonia and Thrace' ELSE "Region" END) COLLATE "C" AS "Region"
                FROM trimmed
            )
            """;
    }

    /// <summary>Counts distinct component tuples in PostgreSQL without loading Location entities.</summary>
    private async Task<UserLocationStatsDto> ReadSummaryAsync(string scope, object[] parameters, CancellationToken token = default)
    {
        // Only trusted projection fragments compose SQL; all owner/cutoff values are parameters.
        var sql = scope + """

            SELECT COUNT(*)::integer AS "TotalLocations",
                COUNT(DISTINCT "Country") FILTER (WHERE "Country" <> '')::integer AS "CountriesVisited",
                COUNT(DISTINCT ("Country", "Region")) FILTER (WHERE "Region" <> '')::integer AS "RegionsVisited",
                COUNT(DISTINCT ("Country", "Region", "Place")) FILTER (WHERE "Place" <> '')::integer AS "CitiesVisited",
                MIN("VisitTime") AS "FromDate", MAX("VisitTime") AS "ToDate"
            FROM scoped
            """;
        var rows = await _db.Database.SqlQueryRaw<UserLocationStatsDto>(sql, parameters).ToListAsync(token);
        return rows.Single();
    }

    /// <summary>Aggregates visits in PostgreSQL and maps only grouped results to unchanged DTOs.</summary>
    private async Task<UserLocationStatsDetailedDto> ReadDetailsAsync(string scope, object[] parameters, CancellationToken token = default)
    {
        var summary = await ReadSummaryAsync(scope, parameters, token);
        var totalLocations = summary.TotalLocations;
        // #977: scope is built only by StatisticsScope/CalendarScope above; every variable value is in parameters.
#pragma warning disable EF1003 // Fixed aggregate SQL plus internal scope fragments; no request text enters SQL syntax.
        var countryGroupsSql = await _db.Database.SqlQueryRaw<CountryGroupResult>(scope + """

            SELECT "Country", MIN("VisitTime") AS "FirstVisit", MAX("VisitTime") AS "LastVisit",
                COUNT(*)::integer AS "VisitCount",
                AVG(ST_X("Coordinates"::geometry) ORDER BY "Id") AS "AvgLongitude",
                AVG(ST_Y("Coordinates"::geometry) ORDER BY "Id") AS "AvgLatitude"
            FROM scoped WHERE "Country" <> '' GROUP BY "Country"
            """, parameters).ToListAsync(token);
#pragma warning restore EF1003
        // Detect home country: country with >40% of total visits or significantly more than average
        var averageVisitCount = countryGroupsSql.Any() ? countryGroupsSql.Average(c => c.VisitCount) : 0;
        var homeCountryThreshold = Math.Max(totalLocations * 0.4, averageVisitCount * 3);

        var countries = countryGroupsSql
            .Select(c => new CountryVisitDetail
            {
                Name = c.Country ?? string.Empty,
                FirstVisit = c.FirstVisit,
                LastVisit = c.LastVisit,
                VisitCount = c.VisitCount,
                IsHomeCountry = c.VisitCount >= homeCountryThreshold,
                Coordinates = c.AvgLongitude is { } longitude && c.AvgLatitude is { } latitude
                    ? new NetTopologySuite.Geometries.Point(longitude, latitude) { SRID = 4326 } : null
            })
            .OrderByDescending(c => c.IsHomeCountry)
            .ThenByDescending(c => c.VisitCount)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        // #977: the same internal scope and bound parameters feed region grouping without interpolated values.
#pragma warning disable EF1003 // Fixed aggregate SQL plus the assessed StatisticsScope/CalendarScope fragments.
        var regionGroupsSql = await _db.Database.SqlQueryRaw<RegionGroupResult>(scope + """

            SELECT "Country", "Region", MIN("VisitTime") AS "FirstVisit", MAX("VisitTime") AS "LastVisit",
                COUNT(*)::integer AS "VisitCount",
                AVG(ST_X("Coordinates"::geometry) ORDER BY "Id") AS "AvgLongitude",
                AVG(ST_Y("Coordinates"::geometry) ORDER BY "Id") AS "AvgLatitude"
            FROM scoped WHERE "Region" <> '' GROUP BY "Country", "Region"
            """, parameters).ToListAsync(token);
#pragma warning restore EF1003
        var regions = regionGroupsSql
            .Select(r => new RegionVisitDetail
            {
                Name = r.Region ?? string.Empty,
                CountryName = r.Country ?? string.Empty,
                FirstVisit = r.FirstVisit,
                LastVisit = r.LastVisit,
                VisitCount = r.VisitCount,
                Coordinates = r.AvgLongitude is { } longitude && r.AvgLatitude is { } latitude
                    ? new NetTopologySuite.Geometries.Point(longitude, latitude) { SRID = 4326 } : null
            })
            .OrderBy(r => r.CountryName, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        // The full partition aggregates all visits; DISTINCT ON selects exactly one coordinate row.
        // #977: partition/order syntax is fixed; scope and parameter provenance are identical to country/region groups.
#pragma warning disable EF1003 // Fixed aggregate SQL plus the assessed StatisticsScope/CalendarScope fragments.
        var cityGroupsSql = await _db.Database.SqlQueryRaw<CityGroupResult>(scope + """

            SELECT DISTINCT ON ("Country", "Region", "Place") "Country", "Region", "Place",
                MIN("VisitTime") OVER membership AS "FirstVisit",
                MAX("VisitTime") OVER membership AS "LastVisit",
                (COUNT(*) OVER membership)::integer AS "VisitCount",
                ST_X("Coordinates"::geometry) AS "RepLongitude",
                ST_Y("Coordinates"::geometry) AS "RepLatitude"
            FROM scoped WHERE "Place" <> ''
            WINDOW membership AS (PARTITION BY "Country", "Region", "Place")
            ORDER BY "Country", "Region", "Place", "VisitTime" DESC, "Id" DESC
            """, parameters).ToListAsync(token);
#pragma warning restore EF1003
        var cities = cityGroupsSql
            .Select(c => new CityVisitDetail
            {
                Name = c.Place ?? string.Empty,
                RegionName = c.Region ?? string.Empty,
                CountryName = c.Country ?? string.Empty,
                FirstVisit = c.FirstVisit,
                LastVisit = c.LastVisit,
                VisitCount = c.VisitCount,
                Coordinates = c.RepLongitude is { } longitude && c.RepLatitude is { } latitude
                    ? new NetTopologySuite.Geometries.Point(longitude, latitude) { SRID = 4326 } : null
            })
            .OrderBy(c => c.CountryName, StringComparer.Ordinal)
            .ThenBy(c => c.RegionName, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        return new UserLocationStatsDetailedDto
        {
            TotalLocations = totalLocations,
            Countries = countries,
            Regions = regions,
            Cities = cities,
            FromDate = summary.FromDate,
            ToDate = summary.ToDate
        };
    }

    /// <summary>
    /// Helper class for country group SQL results
    /// </summary>
    private class CountryGroupResult
    {
        public string Country { get; set; } = string.Empty;
        public DateTime FirstVisit { get; set; }
        public DateTime LastVisit { get; set; }
        public int VisitCount { get; set; }
        public double? AvgLongitude { get; set; }
        public double? AvgLatitude { get; set; }
    }

    /// <summary>
    /// Helper class for region group SQL results
    /// </summary>
    private class RegionGroupResult
    {
        public string Region { get; set; } = string.Empty;
        public string? Country { get; set; }
        public DateTime FirstVisit { get; set; }
        public DateTime LastVisit { get; set; }
        public int VisitCount { get; set; }
        public double? AvgLongitude { get; set; }
        public double? AvgLatitude { get; set; }
    }

    /// <summary>
    /// Helper class for city group SQL results
    /// </summary>
    private class CityGroupResult
    {
        public string Place { get; set; } = string.Empty;
        public string? Region { get; set; }
        public string? Country { get; set; }
        public DateTime FirstVisit { get; set; }
        public DateTime LastVisit { get; set; }
        public int VisitCount { get; set; }
        public double? RepLongitude { get; set; }
        public double? RepLatitude { get; set; }
    }

}
