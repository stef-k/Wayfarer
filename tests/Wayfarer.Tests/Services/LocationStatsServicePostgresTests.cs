using NetTopologySuite.Geometries;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves detailed Timeline statistics through the production PostgreSQL projections.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationStatsServicePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task PopulatedStatistics_ReturnExpectedAllTimeAndDateRangeDetails()
    {
        var user = await fixture.CreateUserAsync();
        var first = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var last = first.AddHours(1);
        await using var context = fixture.CreateContext();
        context.Locations.AddRange(
            Location(user.Id, first, 25.86, 40.84),
            Location(user.Id, last, 25.88, 40.85));
        await context.SaveChangesAsync();
        var service = new LocationStatsService(context);

        var allTime = await service.GetDetailedStatsForUserAsync(user.Id);
        var dateRange = await service.GetDetailedStatsForDateRangeAsync(user.Id, first, last);

        AssertDetails(allTime, first, last);
        AssertDetails(dateRange, first, last);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new PointJsonConverter());
        var json = JsonSerializer.Serialize(allTime, jsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(25.87,
            document.RootElement.GetProperty("countries")[0].GetProperty("coordinates").GetProperty("longitude").GetDouble(),
            precision: 6);
    }

    [PostgresFact]
    public async Task PartialAddressHierarchy_RemainsReadable()
    {
        var user = await fixture.CreateUserAsync();
        var timestamp = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
        await using var context = fixture.CreateContext();
        var countryOnly = Location(user.Id, timestamp, 25.86, 40.84);
        countryOnly.Region = null;
        countryOnly.Place = "Postcode area";
        var localOnly = Location(user.Id, timestamp.AddMinutes(1), 25.88, 40.85);
        localOnly.Country = null;
        context.Locations.AddRange(countryOnly, localOnly);
        await context.SaveChangesAsync();

        var service = new LocationStatsService(context);
        var allTime = await service.GetDetailedStatsForUserAsync(user.Id);
        var dateRange = await service.GetDetailedStatsForDateRangeAsync(user.Id, timestamp, timestamp.AddMinutes(1));

        AssertPartialHierarchy(allTime);
        AssertPartialHierarchy(dateRange);
    }

    private static Location Location(string userId, DateTime timestamp, double longitude, double latitude) => new()
    {
        UserId = userId,
        Timestamp = timestamp,
        LocalTimestamp = timestamp,
        TimeZoneId = "UTC",
        Coordinates = new Point(longitude, latitude) { SRID = 4326 },
        Country = "Greece",
        Region = "Eastern Macedonia and Thrace",
        Place = "Alexandroupolis"
    };

    private static void AssertDetails(UserLocationStatsDetailedDto result, DateTime first, DateTime last)
    {
        Assert.Equal(2, result.TotalLocations);
        Assert.Equal(("Greece", 2), (Assert.Single(result.Countries).Name, result.Countries[0].VisitCount));
        Assert.Equal(("Eastern Macedonia and Thrace", 2),
            (Assert.Single(result.Regions).Name, result.Regions[0].VisitCount));
        Assert.Equal(("Alexandroupolis", 2), (Assert.Single(result.Cities).Name, result.Cities[0].VisitCount));
        Assert.Equal(first, result.FromDate);
        Assert.Equal(last, result.ToDate);
        Assert.NotNull(result.Countries[0].Coordinates);
        Assert.NotNull(result.Regions[0].Coordinates);
        Assert.NotNull(result.Cities[0].Coordinates);
    }

    private static void AssertPartialHierarchy(UserLocationStatsDetailedDto result)
    {
        Assert.Equal(2, result.TotalLocations);
        Assert.Single(result.Countries);
        Assert.Single(result.Regions);
        Assert.Equal(string.Empty, result.Regions[0].CountryName);
        Assert.Equal(2, result.Cities.Count);
        Assert.Contains(result.Cities, city => city.CountryName == string.Empty);
        Assert.Contains(result.Cities, city => city.RegionName == string.Empty);
    }
}
