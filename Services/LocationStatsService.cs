using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

public interface ILocationStatsService
{
    Task<UserLocationStatsDto> GetStatsForUserAsync(string userId);
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
}