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
            .Select(l => l.Country).Distinct().CountAsync();
        var distinctCities = await userLocations
            .Select(l => l.Place).Distinct().CountAsync();
        var distinctRegions = await userLocations
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

        // Get country details with visit counts and date ranges
        var countryGroups = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Country))
            .GroupBy(l => l.Country)
            .Select(g => new
            {
                Country = g.Key,
                FirstVisit = g.Min(l => l.Timestamp),
                LastVisit = g.Max(l => l.Timestamp),
                VisitCount = g.Count(),
                Locations = g.Select(l => l.Coordinates).ToList()
            })
            .ToListAsync();

        // Calculate coordinate averages in memory (geography type doesn't support ST_X/ST_Y directly)
        var countryGroupsWithCoords = countryGroups.Select(c => new
        {
            c.Country,
            c.FirstVisit,
            c.LastVisit,
            c.VisitCount,
            AvgLongitude = c.Locations.Average(coord => coord.X),
            AvgLatitude = c.Locations.Average(coord => coord.Y)
        }).ToList();

        // Detect home country: country with >40% of total visits or significantly more than average
        var averageVisitCount = countryGroupsWithCoords.Any() ? countryGroupsWithCoords.Average(c => c.VisitCount) : 0;
        var homeCountryThreshold = Math.Max(totalLocations * 0.4, averageVisitCount * 3);

        var countries = countryGroupsWithCoords
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

        // Get distinct regions
        var regions = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Region))
            .Select(l => l.Region!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync();

        // Get distinct cities
        var cities = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Place))
            .Select(l => l.Place!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

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

        // Get country details with visit counts and date ranges
        var countryGroups = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Country))
            .GroupBy(l => l.Country)
            .Select(g => new
            {
                Country = g.Key,
                FirstVisit = g.Min(l => l.LocalTimestamp),
                LastVisit = g.Max(l => l.LocalTimestamp),
                VisitCount = g.Count(),
                Locations = g.Select(l => l.Coordinates).ToList()
            })
            .ToListAsync();

        // Calculate coordinate averages in memory (geography type doesn't support ST_X/ST_Y directly)
        var countryGroupsWithCoords = countryGroups.Select(c => new
        {
            c.Country,
            c.FirstVisit,
            c.LastVisit,
            c.VisitCount,
            AvgLongitude = c.Locations.Average(coord => coord.X),
            AvgLatitude = c.Locations.Average(coord => coord.Y)
        }).ToList();

        // Detect home country: country with >40% of total visits or significantly more than average
        var averageVisitCount = countryGroupsWithCoords.Any() ? countryGroupsWithCoords.Average(c => c.VisitCount) : 0;
        var homeCountryThreshold = Math.Max(totalLocations * 0.4, averageVisitCount * 3);

        var countries = countryGroupsWithCoords
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

        // Get distinct regions
        var regions = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Region))
            .Select(l => l.Region!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync();

        // Get distinct cities
        var cities = await userLocations
            .Where(l => !string.IsNullOrEmpty(l.Place))
            .Select(l => l.Place!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

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