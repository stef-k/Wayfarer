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

    /// <summary>Inclusive leap/max bounds select capture time and owner; availability shares the same boundaries.</summary>
    [Theory]
    [InlineData("day", 2024, 2, 29, "2024-02-29T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("month", 2024, 2, null, "2024-02-01T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("year", 2024, null, null, "2024-01-01T00:00:00Z", "2024-12-31T23:59:59.9999999Z")]
    [InlineData("day", 9999, 12, 31, "9999-12-31T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("month", 9999, 12, null, "9999-12-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("year", 9999, null, null, "9999-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    public async Task GetLocationsByDateAsync_PreservesInclusiveOwnerSelection(string mode, int year, int? month,
        int? day, string first, string last)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var other = TestDataFixtures.CreateUser();
        db.Users.AddRange(user, other);
        var start = DateTime.Parse(first, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var end = DateTime.Parse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var firstRow = CreateLocation(user.Id, start, 0, 0);
        var lastRow = CreateLocation(user.Id, end, 0, 0);
        // Receipt time differs: the inclusive predicate belongs to capture/event LocalTimestamp.
        firstRow.Timestamp = lastRow.Timestamp = start.AddDays(-1);
        db.Locations.AddRange(firstRow, lastRow, CreateLocation(user.Id, start.AddTicks(-1), 0, 0),
            CreateLocation(other.Id, end, 0, 0));
        if (year != 9999) db.Locations.Add(CreateLocation(user.Id, end.AddTicks(1), 0, 0));
        await db.SaveChangesAsync();
        var service = new LocationService(db);

        var (locations, totalItems) = await service.GetLocationsByDateAsync(user.Id, mode, year, month, day);

        Assert.Equal(2, totalItems);
        Assert.Equal(new[] { firstRow.Id, lastRow.Id }, locations.Select(l => l.Id));
        Assert.True(await service.HasDataForDateAsync(user.Id, end));
        Assert.True(await service.HasDataForMonthAsync(user.Id, end.Year, end.Month));
        Assert.False(await service.HasDataForDateAsync("absent-owner", end));
        Assert.False(await service.HasDataForMonthAsync("absent-owner", end.Year, end.Month));
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

        // Assert - The effective live window uses the ingestion default of five minutes.
        Assert.Single(locations);
        Assert.Equal(5, locations[0].LocationTimeThresholdMinutes);
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
