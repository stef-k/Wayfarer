using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="LocationService"/> covering location retrieval and date navigation.
/// Note: Tests focus on EF-based methods since raw SQL/PostGIS methods require a real database.
/// </summary>
public class LocationServiceTests : TestBase
{
    #region GetLocationsByDateAsync Tests

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsLocations_ForSpecificDay()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var targetDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var location1 = CreateLocation(user.Id, targetDate, 40.7128, -74.0060);
        var location2 = CreateLocation(user.Id, targetDate.AddHours(2), 40.7130, -74.0062);
        var locationOtherDay = CreateLocation(user.Id, targetDate.AddDays(1), 40.7140, -74.0070);

        db.Locations.AddRange(location1, location2, locationOtherDay);
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user.Id, "day", 2024, 6, 15);

        // Assert
        Assert.Equal(2, totalItems);
        Assert.Equal(2, locations.Count);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsLocations_ForSpecificMonth()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var june1 = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var june15 = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var july1 = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        db.Locations.Add(CreateLocation(user.Id, june1, 40.71, -74.00));
        db.Locations.Add(CreateLocation(user.Id, june15, 40.72, -74.01));
        db.Locations.Add(CreateLocation(user.Id, july1, 40.73, -74.02));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user.Id, "month", 2024, 6);

        // Assert
        Assert.Equal(2, totalItems);
        Assert.Equal(2, locations.Count);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsLocations_ForSpecificYear()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var jan2024 = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var dec2024 = new DateTime(2024, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var jan2025 = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        db.Locations.Add(CreateLocation(user.Id, jan2024, 40.71, -74.00));
        db.Locations.Add(CreateLocation(user.Id, dec2024, 40.72, -74.01));
        db.Locations.Add(CreateLocation(user.Id, jan2025, 40.73, -74.02));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user.Id, "year", 2024);

        // Assert
        Assert.Equal(2, totalItems);
        Assert.Equal(2, locations.Count);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ThrowsException_ForInvalidDateType()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("user-id", "invalid", 2024));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ThrowsException_WhenMonthMissingForDayFilter()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("user-id", "day", 2024, day: 15));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ThrowsException_WhenDayMissingForDayFilter()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("user-id", "day", 2024, month: 6));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ThrowsException_WhenMonthMissingForMonthFilter()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("user-id", "month", 2024));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsEmptyList_WhenNoData()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            "nonexistent-user", "day", 2024, 6, 15);

        // Assert
        Assert.Empty(locations);
        Assert.Equal(0, totalItems);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_FiltersOnlyUserLocations()
    {
        // Arrange
        var db = CreateDbContext();
        var user1 = TestDataFixtures.CreateUser();
        var user2 = TestDataFixtures.CreateUser();
        db.Users.AddRange(user1, user2);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user1.Id, date, 40.71, -74.00));
        db.Locations.Add(CreateLocation(user2.Id, date, 40.72, -74.01));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user1.Id, "day", 2024, 6, 15);

        // Assert
        Assert.Equal(1, totalItems);
        Assert.Single(locations);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_IncludesActivityType()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var activityType = new ActivityType { Id = 1, Name = "Walking" };
        db.Users.Add(user);
        db.ActivityTypes.Add(activityType);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var location = CreateLocation(user.Id, date, 40.71, -74.00);
        location.ActivityTypeId = activityType.Id;
        location.ActivityType = activityType;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user.Id, "day", 2024, 6, 15);

        // Assert
        Assert.Single(locations);
        Assert.Equal("Walking", locations[0].ActivityType);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_UsesDefaultActivityType_WhenNull()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var location = CreateLocation(user.Id, date, 40.71, -74.00);
        location.ActivityTypeId = null;
        location.ActivityType = null;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, totalItems) = await service.GetLocationsByDateAsync(
            user.Id, "day", 2024, 6, 15);

        // Assert
        Assert.Single(locations);
        Assert.Equal("Unknown", locations[0].ActivityType);
    }

    #endregion

    #region HasDataForDateAsync Tests

    [Fact]
    public async Task HasDataForDateAsync_ReturnsTrue_WhenDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForDateAsync(user.Id, new DateTime(2024, 6, 15));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasDataForDateAsync_ReturnsFalse_WhenNoDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForDateAsync("user-id", new DateTime(2024, 6, 15));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task HasDataForDateAsync_ChecksEntireDay()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Location at end of day
        var endOfDay = new DateTime(2024, 6, 15, 23, 59, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, endOfDay, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForDateAsync(user.Id, new DateTime(2024, 6, 15));

        // Assert
        Assert.True(result);
    }

    #endregion

    #region HasDataForMonthAsync Tests

    [Fact]
    public async Task HasDataForMonthAsync_ReturnsTrue_WhenDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForMonthAsync(user.Id, 2024, 6);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasDataForMonthAsync_ReturnsFalse_WhenNoDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForMonthAsync("user-id", 2024, 6);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task HasDataForMonthAsync_ChecksEntireMonth()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Location at end of month
        var endOfJune = new DateTime(2024, 6, 30, 23, 59, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, endOfJune, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForMonthAsync(user.Id, 2024, 6);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region HasDataForYearAsync Tests

    [Fact]
    public async Task HasDataForYearAsync_ReturnsTrue_WhenDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForYearAsync(user.Id, 2024);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasDataForYearAsync_ReturnsFalse_WhenNoDataExists()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForYearAsync("user-id", 2024);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task HasDataForYearAsync_ChecksEntireYear()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Location at end of year
        var endOfYear = new DateTime(2024, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, endOfYear, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForYearAsync(user.Id, 2024);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasDataForYearAsync_ReturnsFalse_ForDifferentYear()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var result = await service.HasDataForYearAsync(user.Id, 2025);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region ApplicationSettings Integration Tests

    [Fact]
    public async Task GetLocationsByDateAsync_UsesDefaultThreshold_WhenNoSettings()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, _) = await service.GetLocationsByDateAsync(
            user.Id, "day", 2024, 6, 15);

        // Assert - Default is 10 minutes
        Assert.Single(locations);
        Assert.Equal(10, locations[0].LocationTimeThresholdMinutes);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_UsesCustomThreshold_FromSettings()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 30
        };
        db.Users.Add(user);
        db.ApplicationSettings.Add(settings);

        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Locations.Add(CreateLocation(user.Id, date, 40.71, -74.00));
        await db.SaveChangesAsync();

        var service = new LocationService(db);

        // Act
        var (locations, _) = await service.GetLocationsByDateAsync(
            user.Id, "day", 2024, 6, 15);

        // Assert
        Assert.Single(locations);
        Assert.Equal(30, locations[0].LocationTimeThresholdMinutes);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesWithDbContext()
    {
        // Arrange
        var db = CreateDbContext();

        // Act
        var service = new LocationService(db);

        // Assert
        Assert.NotNull(service);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a location entity for testing.
    /// </summary>
    private static Wayfarer.Models.Location CreateLocation(string userId, DateTime timestamp, double lat, double lon)
    {
        return new Wayfarer.Models.Location
        {
            UserId = userId,
            Coordinates = new Point(lon, lat) { SRID = 4326 },
            Timestamp = timestamp,
            LocalTimestamp = timestamp,
            TimeZoneId = "UTC",
            Accuracy = 10,
            LocationType = "checkin"
        };
    }

    #endregion
}
