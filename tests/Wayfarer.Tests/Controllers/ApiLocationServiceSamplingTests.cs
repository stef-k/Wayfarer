using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// LocationService sampling guards for invalid inputs.
/// </summary>
public class ApiLocationServiceSamplingTests : TestBase
{
    [Fact]
    public async Task GetLocationsByDateAsync_Throws_WhenMonthMissing()
    {
        var db = CreateDbContext();
        var service = new LocationService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("u", "month", 2024));
    }

    [Fact]
    public async Task GetLocationsByDateAsync_Throws_WhenDayMissing()
    {
        var db = CreateDbContext();
        var service = new LocationService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLocationsByDateAsync("u", "day", 2024, 6));
    }
}
