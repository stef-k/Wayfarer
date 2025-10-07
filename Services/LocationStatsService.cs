using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Parsers;

public interface ILocationStatsService
{
    Task<UserLocationStatsDto> GetStatsForUserAsync(string userId);
    Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate);
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
}