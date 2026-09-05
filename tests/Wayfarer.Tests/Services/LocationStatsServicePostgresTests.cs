using NetTopologySuite.Geometries;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>One dataset proves corrected membership, scope, representatives and read-only behavior.</summary>
    [PostgresFact]
    public async Task CorrectedGroups_AgreeAcrossScopes_AndPreserveStoredRows()
    {
        var user = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var start = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        await using var context = fixture.CreateContext();
        var labels = new (string? Country, string? Region, string? Place)[]
        {
            (" Greece ", " East Macedonia and Thrace\t", " Port "),
            ("Greece", "Eastern Macedonia and Thrace", "Port"),
            ("Other", "East Macedonia and Thrace", "Port"),
            ("Other", "Eastern Macedonia and Thrace", "Port"),
            (null, "East Macedonia and Thrace", "Port"),
            ("", null, "Port"),
            (" \t", "\r\n", "Port"),
            ("Greece", null, "Port"),
            ("Greece", "Another region", "Port"),
            (null, null, null)
        };
        var rows = labels.Select((label, index) =>
        {
            var row = Location(user.Id, start, 20 + index, 40);
            (row.Country, row.Region, row.Place) = label;
            row.LocalTimestamp = index == 8 ? start.AddDays(1) : start.AddMinutes(index % 2);
            row.FullAddress = $"Original {index}";
            row.ProviderAddressLine1 = $"Provider {index}";
            return row;
        }).ToArray();
        context.Locations.AddRange(rows);
        context.Locations.Add(Location(other.Id, start, 99, 40));
        await context.SaveChangesAsync();
        var before = await context.Locations.AsNoTracking().Where(l => l.UserId == user.Id)
            .OrderBy(l => l.Id).ToListAsync();
        var service = new LocationStatsService(context);

        var all = await service.GetDetailedStatsForUserAsync(user.Id);
        var summary = await service.GetStatsForUserAsync(user.Id);
        var window = await service.GetDetailedStatsForDateRangeAsync(user.Id, start, start.AddMinutes(1));
        var windowSummary = await service.GetStatsForDateRangeAsync(user.Id, start, start.AddMinutes(1));
        AssertAgreement(summary, all, 10, 2, 5, 7);
        AssertAgreement(windowSummary, window, 9, 2, 4, 6);
        foreach (var detail in new[] { all, window })
        {
            var region = Assert.Single(detail.Regions.Where(r => r.CountryName == "Greece" &&
                r.Name == "Eastern Macedonia and Thrace"));
            Assert.Equal(2, region.VisitCount);
            Assert.Equal(20.5, region.Coordinates!.X);
            var city = Assert.Single(detail.Cities.Where(c => c.CountryName == "Greece" &&
                c.RegionName == region.Name && c.Name == "Port"));
            Assert.Equal(2, city.VisitCount);
            Assert.Equal(21, city.Coordinates!.X);
            Assert.Equal(2, Assert.Single(detail.Cities.Where(c => c.CountryName == "" && c.RegionName == "")).VisitCount);
        }
        Assert.Equal(start, all.ToDate);
        Assert.Equal(start.AddMinutes(1), window.ToDate);
        Assert.Equal(1, (await service.GetStatsForUserAsync(other.Id)).TotalLocations);
        var after = await context.Locations.AsNoTracking().Where(l => l.UserId == user.Id)
            .OrderBy(l => l.Id).ToListAsync();
        // Compare every mapped scalar, including retained provider/feature fields, from fresh database reads.
        var properties = context.Model.FindEntityType(typeof(Location))!.GetProperties();
        foreach (var property in properties.Where(p => p.PropertyInfo != null))
            Assert.Equal(before.Select(row => property.PropertyInfo!.GetValue(row)),
                after.Select(row => property.PropertyInfo!.GetValue(row)));
    }

    /// <summary>Summary counts describe exactly the detailed arrays, without synthetic parents.</summary>
    private static void AssertAgreement(UserLocationStatsDto summary, UserLocationStatsDetailedDto detail,
        int locations, int countries, int regions, int cities)
    {
        Assert.Equal((locations, countries, regions, cities),
            (summary.TotalLocations, summary.CountriesVisited, summary.RegionsVisited, summary.CitiesVisited));
        Assert.Equal((locations, countries, regions, cities),
            (detail.TotalLocations, detail.Countries.Count, detail.Regions.Count, detail.Cities.Count));
        Assert.Equal((summary.FromDate, summary.ToDate), (detail.FromDate, detail.ToDate));
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
