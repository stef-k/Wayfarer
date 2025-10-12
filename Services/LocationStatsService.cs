using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Parsers;

public interface ILocationStatsService
{
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

    public async Task<UserLocationStatsDto> GetStatsForUserAsync(string userId)
    {
        var userLocations = _db.Locations.Where(l => l.UserId == userId);

        var totalLocations = await userLocations.CountAsync();
        var distinctCountries = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Country))
            .Select(l => l.Country).Distinct().CountAsync();
        var distinctCities = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Place))
            .Select(l => l.Place).Distinct().CountAsync();
        var distinctRegions = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Region))
            .Select(l => l.Region).Distinct().CountAsync();

        var fromDate = await userLocations.MinAsync(l => (DateTime?)l.Timestamp);

        var toDate = await userLocations.MaxAsync(l => (DateTime?)l.Timestamp);

        return new UserLocationStatsDto
        {
            TotalLocations = totalLocations,
            CountriesVisited = distinctCountries,
            CitiesVisited = distinctCities,
            RegionsVisited = distinctRegions,
            FromDate = fromDate,
            ToDate = toDate
        };
    }

    /// <summary>
    /// Gets statistics for a specific date range (day, month, or year)
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="startDate">Start date (UTC)</param>
    /// <param name="endDate">End date (UTC)</param>
    /// <returns>Statistics for the date range</returns>
    public async Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate)
    {
        var userLocations = _db.Locations.Where(l => l.UserId == userId
                                                      && l.LocalTimestamp >= startDate
                                                      && l.LocalTimestamp <= endDate);

        var totalLocations = await userLocations.CountAsync();
        var distinctCountries = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Country))
            .Select(l => l.Country).Distinct().CountAsync();
        var distinctCities = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Place))
            .Select(l => l.Place).Distinct().CountAsync();
        var distinctRegions = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Region))
            .Select(l => l.Region).Distinct().CountAsync();

        var fromDate = await userLocations.MinAsync(l => (DateTime?)l.LocalTimestamp);
        var toDate = await userLocations.MaxAsync(l => (DateTime?)l.LocalTimestamp);

        return new UserLocationStatsDto
        {
            TotalLocations = totalLocations,
            CountriesVisited = distinctCountries,
            CitiesVisited = distinctCities,
            RegionsVisited = distinctRegions,
            FromDate = fromDate,
            ToDate = toDate
        };
    }

    /// <summary>
    /// Gets detailed statistics for all user locations including country details, regions, and cities
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <returns>Detailed statistics with arrays of country names, regions, and cities</returns>
    public async Task<UserLocationStatsDetailedDto> GetDetailedStatsForUserAsync(string userId)
    {
        var userLocations = _db.Locations.Where(l => l.UserId == userId);

        var totalLocations = await userLocations.CountAsync();

        // OPTIMIZED: Get country details with coordinate averages calculated in database using PostGIS
        // Using raw SQL for PostGIS functions - cast geography to geometry to use ST_X/ST_Y
        var countryGroupsSql = await _db.Database.SqlQueryRaw<CountryGroupResult>(@"
            SELECT
                ""Country"",
                MIN(""Timestamp"") as ""FirstVisit"",
                MAX(""Timestamp"") as ""LastVisit"",
                COUNT(*) as ""VisitCount"",
                AVG(ST_X(""Coordinates""::geometry)) as ""AvgLongitude"",
                AVG(ST_Y(""Coordinates""::geometry)) as ""AvgLatitude""
            FROM ""Locations""
            WHERE ""UserId"" = {0} AND ""Country"" IS NOT NULL AND ""Country"" != ''
            GROUP BY ""Country""
        ", userId).ToListAsync();

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
                Coordinates = new NetTopologySuite.Geometries.Point(c.AvgLongitude, c.AvgLatitude) { SRID = 4326 }
            })
            .OrderByDescending(c => c.IsHomeCountry)
            .ThenByDescending(c => c.VisitCount)
            .ToList();

        // OPTIMIZED: Get region details with coordinate averages calculated in database using PostGIS
        var regionGroupsSql = await _db.Database.SqlQueryRaw<RegionGroupResult>(@"
            SELECT
                ""Region"",
                ""Country"",
                MIN(""Timestamp"") as ""FirstVisit"",
                MAX(""Timestamp"") as ""LastVisit"",
                COUNT(*) as ""VisitCount"",
                AVG(ST_X(""Coordinates""::geometry)) as ""AvgLongitude"",
                AVG(ST_Y(""Coordinates""::geometry)) as ""AvgLatitude""
            FROM ""Locations""
            WHERE ""UserId"" = {0} AND ""Region"" IS NOT NULL AND ""Region"" != ''
            GROUP BY ""Region"", ""Country""
        ", userId).ToListAsync();

        var regions = regionGroupsSql
            .Select(r => new RegionVisitDetail
            {
                Name = r.Region ?? string.Empty,
                CountryName = r.Country ?? string.Empty,
                FirstVisit = r.FirstVisit,
                LastVisit = r.LastVisit,
                VisitCount = r.VisitCount,
                Coordinates = new NetTopologySuite.Geometries.Point(r.AvgLongitude, r.AvgLatitude) { SRID = 4326 }
            })
            .OrderBy(r => r.CountryName)
            .ThenBy(r => r.Name)
            .ToList();

        // OPTIMIZED: Get city details with representative coordinate (from most recent visit)
        // This avoids loading thousands of coordinates into memory
        var cityGroupsSql = await _db.Database.SqlQueryRaw<CityGroupResult>(@"
            WITH CityLatest AS (
                SELECT
                    ""Place"",
                    ""Region"",
                    ""Country"",
                    MIN(""Timestamp"") as ""FirstVisit"",
                    MAX(""Timestamp"") as ""LastVisit"",
                    COUNT(*) as ""VisitCount"",
                    MAX(""Timestamp"") as ""MostRecentVisit""
                FROM ""Locations""
                WHERE ""UserId"" = {0} AND ""Place"" IS NOT NULL AND ""Place"" != ''
                GROUP BY ""Place"", ""Region"", ""Country""
            )
            SELECT
                cl.""Place"",
                cl.""Region"",
                cl.""Country"",
                cl.""FirstVisit"",
                cl.""LastVisit"",
                cl.""VisitCount"",
                ST_X(l.""Coordinates""::geometry) as ""RepLongitude"",
                ST_Y(l.""Coordinates""::geometry) as ""RepLatitude""
            FROM CityLatest cl
            INNER JOIN ""Locations"" l ON
                l.""UserId"" = {0} AND
                l.""Place"" = cl.""Place"" AND
                COALESCE(l.""Region"", '') = COALESCE(cl.""Region"", '') AND
                COALESCE(l.""Country"", '') = COALESCE(cl.""Country"", '') AND
                l.""Timestamp"" = cl.""MostRecentVisit""
        ", userId).ToListAsync();

        var cities = cityGroupsSql
            .Select(c => new CityVisitDetail
            {
                Name = c.Place ?? string.Empty,
                RegionName = c.Region ?? string.Empty,
                CountryName = c.Country ?? string.Empty,
                FirstVisit = c.FirstVisit,
                LastVisit = c.LastVisit,
                VisitCount = c.VisitCount,
                Coordinates = new NetTopologySuite.Geometries.Point(c.RepLongitude, c.RepLatitude) { SRID = 4326 }
            })
            .OrderBy(c => c.CountryName)
            .ThenBy(c => c.RegionName)
            .ThenBy(c => c.Name)
            .ToList();

        var fromDate = await userLocations.MinAsync(l => (DateTime?)l.Timestamp);
        var toDate = await userLocations.MaxAsync(l => (DateTime?)l.Timestamp);

        return new UserLocationStatsDetailedDto
        {
            TotalLocations = totalLocations,
            Countries = countries,
            Regions = regions,
            Cities = cities,
            FromDate = fromDate,
            ToDate = toDate
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
        public double AvgLongitude { get; set; }
        public double AvgLatitude { get; set; }
    }

    /// <summary>
    /// Helper class for region group SQL results
    /// </summary>
    private class RegionGroupResult
    {
        public string Region { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public DateTime FirstVisit { get; set; }
        public DateTime LastVisit { get; set; }
        public int VisitCount { get; set; }
        public double AvgLongitude { get; set; }
        public double AvgLatitude { get; set; }
    }

    /// <summary>
    /// Helper class for city group SQL results
    /// </summary>
    private class CityGroupResult
    {
        public string Place { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public DateTime FirstVisit { get; set; }
        public DateTime LastVisit { get; set; }
        public int VisitCount { get; set; }
        public double RepLongitude { get; set; }
        public double RepLatitude { get; set; }
    }

    /// <summary>
    /// Gets detailed statistics for a specific date range including country details, regions, and cities
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="startDate">Start date (UTC)</param>
    /// <param name="endDate">End date (UTC)</param>
    /// <returns>Detailed statistics for the date range with arrays of country names, regions, and cities</returns>
    public async Task<UserLocationStatsDetailedDto> GetDetailedStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate)
    {
        var userLocations = _db.Locations.Where(l => l.UserId == userId
                                                      && l.LocalTimestamp >= startDate
                                                      && l.LocalTimestamp <= endDate);

        var totalLocations = await userLocations.CountAsync();

        // OPTIMIZED: Get country details with coordinate averages calculated in database using PostGIS
        var countryGroupsSql = await _db.Database.SqlQueryRaw<CountryGroupResult>(@"
            SELECT
                ""Country"",
                MIN(""LocalTimestamp"") as ""FirstVisit"",
                MAX(""LocalTimestamp"") as ""LastVisit"",
                COUNT(*) as ""VisitCount"",
                AVG(ST_X(""Coordinates""::geometry)) as ""AvgLongitude"",
                AVG(ST_Y(""Coordinates""::geometry)) as ""AvgLatitude""
            FROM ""Locations""
            WHERE ""UserId"" = {0} AND ""Country"" IS NOT NULL AND ""Country"" != ''
                AND ""LocalTimestamp"" >= {1} AND ""LocalTimestamp"" <= {2}
            GROUP BY ""Country""
        ", userId, startDate, endDate).ToListAsync();

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
                Coordinates = new NetTopologySuite.Geometries.Point(c.AvgLongitude, c.AvgLatitude) { SRID = 4326 }
            })
            .OrderByDescending(c => c.IsHomeCountry)
            .ThenByDescending(c => c.VisitCount)
            .ToList();

        // OPTIMIZED: Get region details with coordinate averages calculated in database using PostGIS
        var regionGroupsSql = await _db.Database.SqlQueryRaw<RegionGroupResult>(@"
            SELECT
                ""Region"",
                ""Country"",
                MIN(""LocalTimestamp"") as ""FirstVisit"",
                MAX(""LocalTimestamp"") as ""LastVisit"",
                COUNT(*) as ""VisitCount"",
                AVG(ST_X(""Coordinates""::geometry)) as ""AvgLongitude"",
                AVG(ST_Y(""Coordinates""::geometry)) as ""AvgLatitude""
            FROM ""Locations""
            WHERE ""UserId"" = {0} AND ""Region"" IS NOT NULL AND ""Region"" != ''
                AND ""LocalTimestamp"" >= {1} AND ""LocalTimestamp"" <= {2}
            GROUP BY ""Region"", ""Country""
        ", userId, startDate, endDate).ToListAsync();

        var regions = regionGroupsSql
            .Select(r => new RegionVisitDetail
            {
                Name = r.Region ?? string.Empty,
                CountryName = r.Country ?? string.Empty,
                FirstVisit = r.FirstVisit,
                LastVisit = r.LastVisit,
                VisitCount = r.VisitCount,
                Coordinates = new NetTopologySuite.Geometries.Point(r.AvgLongitude, r.AvgLatitude) { SRID = 4326 }
            })
            .OrderBy(r => r.CountryName)
            .ThenBy(r => r.Name)
            .ToList();

        // OPTIMIZED: Get city details with representative coordinate (from most recent visit)
        var cityGroupsSql = await _db.Database.SqlQueryRaw<CityGroupResult>(@"
            WITH CityLatest AS (
                SELECT
                    ""Place"",
                    ""Region"",
                    ""Country"",
                    MIN(""LocalTimestamp"") as ""FirstVisit"",
                    MAX(""LocalTimestamp"") as ""LastVisit"",
                    COUNT(*) as ""VisitCount"",
                    MAX(""LocalTimestamp"") as ""MostRecentVisit""
                FROM ""Locations""
                WHERE ""UserId"" = {0} AND ""Place"" IS NOT NULL AND ""Place"" != ''
                    AND ""LocalTimestamp"" >= {1} AND ""LocalTimestamp"" <= {2}
                GROUP BY ""Place"", ""Region"", ""Country""
            )
            SELECT
                cl.""Place"",
                cl.""Region"",
                cl.""Country"",
                cl.""FirstVisit"",
                cl.""LastVisit"",
                cl.""VisitCount"",
                ST_X(l.""Coordinates""::geometry) as ""RepLongitude"",
                ST_Y(l.""Coordinates""::geometry) as ""RepLatitude""
            FROM CityLatest cl
            INNER JOIN ""Locations"" l ON
                l.""UserId"" = {0} AND
                l.""Place"" = cl.""Place"" AND
                COALESCE(l.""Region"", '') = COALESCE(cl.""Region"", '') AND
                COALESCE(l.""Country"", '') = COALESCE(cl.""Country"", '') AND
                l.""LocalTimestamp"" = cl.""MostRecentVisit""
        ", userId, startDate, endDate).ToListAsync();

        var cities = cityGroupsSql
            .Select(c => new CityVisitDetail
            {
                Name = c.Place ?? string.Empty,
                RegionName = c.Region ?? string.Empty,
                CountryName = c.Country ?? string.Empty,
                FirstVisit = c.FirstVisit,
                LastVisit = c.LastVisit,
                VisitCount = c.VisitCount,
                Coordinates = new NetTopologySuite.Geometries.Point(c.RepLongitude, c.RepLatitude) { SRID = 4326 }
            })
            .OrderBy(c => c.CountryName)
            .ThenBy(c => c.RegionName)
            .ThenBy(c => c.Name)
            .ToList();

        var fromDate = await userLocations.MinAsync(l => (DateTime?)l.LocalTimestamp);
        var toDate = await userLocations.MaxAsync(l => (DateTime?)l.LocalTimestamp);

        return new UserLocationStatsDetailedDto
        {
            TotalLocations = totalLocations,
            Countries = countries,
            Regions = regions,
            Cities = cities,
            FromDate = fromDate,
            ToDate = toDate
        };
    }
}