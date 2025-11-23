using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// LocationService date-based query branches.
/// </summary>
public class LocationServiceDateTests : TestBase
{
    [Fact]
    public async Task GetLocationsByDateAsync_Throws_OnInvalidDateType()
    {
        var db = CreateDbContext();
        var service = new LocationService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("user", "invalid", 2024));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsForDay()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.Add(user);
        db.Locations.Add(TestDataFixtures.CreateLocation(user, timestamp: new DateTime(2024, 6, 1)));
        db.SaveChanges();
        var service = new LocationService(db);

        var (locs, total) = await service.GetLocationsByDateAsync("u", "day", 2024, 6, 1);

        Assert.Single(locs);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsForMonth()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.Add(user);
        db.Locations.Add(TestDataFixtures.CreateLocation(user, timestamp: new DateTime(2024, 6, 15)));
        db.SaveChanges();
        var service = new LocationService(db);

        var (locs, total) = await service.GetLocationsByDateAsync("u", "month", 2024, 6);

        Assert.Single(locs);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetLocationsByDateAsync_ReturnsForYear()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.Add(user);
        db.Locations.Add(TestDataFixtures.CreateLocation(user, timestamp: new DateTime(2024, 1, 1)));
        db.SaveChanges();
        var service = new LocationService(db);

        var (locs, total) = await service.GetLocationsByDateAsync("u", "year", 2024);

        Assert.Single(locs);
        Assert.Equal(1, total);
    }
}
