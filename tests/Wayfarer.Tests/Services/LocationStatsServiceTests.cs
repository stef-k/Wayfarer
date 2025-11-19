using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="LocationStatsService"/> covering basic statistics calculations.
/// Note: Detailed stats tests (GetDetailedStatsForUserAsync, GetDetailedStatsForDateRangeAsync)
/// are skipped because they use raw SQL with PostGIS functions that require PostgreSQL.
/// </summary>
public class LocationStatsServiceTests : TestBase
{
    #region GetStatsForUserAsync Tests

    [Fact]
    public async Task GetStatsForUserAsync_ReturnsZeroStats_ForUserWithNoLocations()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(0, result.TotalLocations);
        Assert.Equal(0, result.CountriesVisited);
        Assert.Equal(0, result.CitiesVisited);
        Assert.Equal(0, result.RegionsVisited);
        Assert.Null(result.FromDate);
        Assert.Null(result.ToDate);
    }

    [Fact]
    public async Task GetStatsForUserAsync_CountsAllLocations()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-2)),
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", DateTime.UtcNow)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(3, result.TotalLocations);
    }

    [Fact]
    public async Task GetStatsForUserAsync_CountsDistinctCountries()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-3)),
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", DateTime.UtcNow.AddDays(-2)),
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, "Germany", "Berlin", "Berlin", DateTime.UtcNow)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(3, result.CountriesVisited); // USA, France, Germany
    }

    [Fact]
    public async Task GetStatsForUserAsync_CountsDistinctCities()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-3)),
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-2)), // duplicate city
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", DateTime.UtcNow)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(3, result.CitiesVisited); // New York, Los Angeles, Paris
    }

    [Fact]
    public async Task GetStatsForUserAsync_CountsDistinctRegions()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-3)),
            CreateLocation(user.Id, "USA", "Buffalo", "NY", DateTime.UtcNow.AddDays(-2)), // same region
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", DateTime.UtcNow)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(3, result.RegionsVisited); // NY, CA, Île-de-France
    }

    [Fact]
    public async Task GetStatsForUserAsync_ReturnsCorrectDateRange()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var oldestDate = DateTime.UtcNow.AddDays(-30);
        var newestDate = DateTime.UtcNow;

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", oldestDate),
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", DateTime.UtcNow.AddDays(-15)),
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", newestDate)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.NotNull(result.FromDate);
        Assert.NotNull(result.ToDate);
        Assert.Equal(oldestDate, result.FromDate.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(newestDate, result.ToDate.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetStatsForUserAsync_IgnoresNullCountries()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, null, "Unknown City", "Unknown", DateTime.UtcNow) // null country
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(2, result.TotalLocations);
        Assert.Equal(1, result.CountriesVisited); // only USA
    }

    [Fact]
    public async Task GetStatsForUserAsync_IgnoresEmptyStrings()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user.Id, "", "", "", DateTime.UtcNow) // empty strings
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user.Id);

        // Assert
        Assert.Equal(2, result.TotalLocations);
        Assert.Equal(1, result.CountriesVisited);
        Assert.Equal(1, result.CitiesVisited);
        Assert.Equal(1, result.RegionsVisited);
    }

    [Fact]
    public async Task GetStatsForUserAsync_OnlyCountsUserOwnLocations()
    {
        // Arrange
        var db = CreateDbContext();
        var user1 = TestDataFixtures.CreateUser();
        var user2 = TestDataFixtures.CreateUser();
        db.Users.AddRange(user1, user2);

        var locations = new[]
        {
            CreateLocation(user1.Id, "USA", "New York", "NY", DateTime.UtcNow.AddDays(-1)),
            CreateLocation(user2.Id, "France", "Paris", "Île-de-France", DateTime.UtcNow)
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForUserAsync(user1.Id);

        // Assert
        Assert.Equal(1, result.TotalLocations);
        Assert.Equal(1, result.CountriesVisited);
    }

    #endregion

    #region GetStatsForDateRangeAsync Tests

    [Fact]
    public async Task GetStatsForDateRangeAsync_FiltersToDateRange()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", new DateTime(2023, 12, 15, 0, 0, 0, DateTimeKind.Utc)), // before range
            CreateLocation(user.Id, "USA", "Los Angeles", "CA", new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc)), // in range
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc)), // in range
            CreateLocation(user.Id, "Germany", "Berlin", "Berlin", new DateTime(2024, 2, 5, 0, 0, 0, DateTimeKind.Utc)) // after range
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        Assert.Equal(2, result.TotalLocations);
        Assert.Equal(2, result.CountriesVisited); // USA, France
    }

    [Fact]
    public async Task GetStatsForDateRangeAsync_ReturnsZeroForEmptyRange()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Range that doesn't include any locations
        var startDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        // Act
        var result = await service.GetStatsForDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        Assert.Equal(0, result.TotalLocations);
        Assert.Equal(0, result.CountriesVisited);
        Assert.Null(result.FromDate);
        Assert.Null(result.ToDate);
    }

    [Fact]
    public async Task GetStatsForDateRangeAsync_IncludesEdgeDates()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        var locations = new[]
        {
            CreateLocation(user.Id, "USA", "New York", "NY", startDate), // exactly at start
            CreateLocation(user.Id, "France", "Paris", "Île-de-France", endDate) // exactly at end
        };
        db.Locations.AddRange(locations);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        Assert.Equal(2, result.TotalLocations);
    }

    [Fact]
    public async Task GetStatsForDateRangeAsync_UsesLocalTimestamp()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        // Create location with specific local timestamp
        var location = CreateLocation(user.Id, "USA", "New York", "NY", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        // Override LocalTimestamp to be within range (simulating timezone difference)
        location.LocalTimestamp = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var service = new LocationStatsService(db);

        // Act
        var result = await service.GetStatsForDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        Assert.Equal(1, result.TotalLocations);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a location entity for testing.
    /// </summary>
    private static Wayfarer.Models.Location CreateLocation(
        string userId,
        string? country,
        string? place,
        string? region,
        DateTime timestamp)
    {
        return new Wayfarer.Models.Location
        {
            UserId = userId,
            Timestamp = timestamp,
            LocalTimestamp = timestamp,
            TimeZoneId = "UTC",
            Coordinates = new Point(-74.006, 40.7128) { SRID = 4326 },
            Country = country,
            Place = place,
            Region = region
        };
    }

    #endregion
}
