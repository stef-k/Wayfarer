using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Locks bounded, ordered, domain-state-resumable Geoapify backfill selection.</summary>
public sealed class GeoapifyLocationBackfillTests
{
    [Fact]
    public async Task CandidateSelectionIsOwnedChronologicalWhollyEmptyAndBounded()
    {
        await using var db = CreateDb();
        for (var index = 0; index < 105; index++) db.Locations.Add(Location("user", index));
        db.Locations.Add(Location("other", -1));
        var manual = Location("user", -2); manual.Place = "Manual"; db.Locations.Add(manual);
        await db.SaveChangesAsync();

        var candidates = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(
            db, "user", GeoapifyLocationBackfillService.MaximumRecords);

        Assert.Equal(10, candidates.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(index => index + 1), candidates);
    }

    [Fact]
    public void CandidatePredicateRejectsAnyFieldOrProvenance()
    {
        var fields = new Action<Location>[]
        {
            value => value.Address = "x", value => value.FullAddress = "x", value => value.AddressNumber = "x",
            value => value.StreetName = "x", value => value.PostCode = "x", value => value.Place = "x",
            value => value.Region = "x", value => value.Country = "x",
            value => value.ReverseGeocodingProvider = "geoapify",
            value => value.ReverseGeocodingStorageMode = "persistent",
            value => value.ReverseGeocodedAt = DateTimeOffset.UtcNow
        };

        Assert.All(fields, mutate =>
        {
            var location = Location("user", 0); mutate(location);
            Assert.False(GeoapifyLocationBackfillService.IsWhollyUnenriched(location));
        });
    }

    private static Location Location(string userId, int minute) => new()
    {
        UserId = userId, Timestamp = new DateTime(2026, 1, 1).AddMinutes(minute), LocalTimestamp = new DateTime(2026, 1, 1),
        TimeZoneId = "UTC", Coordinates = new Point(20, 10)
    };

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }
}
