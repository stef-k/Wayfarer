using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies PostgreSQL applies the canonical null-last Trip Editor place order.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripEditorPlaceOrderingPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task MutationReaderOrdersNullableDisplayOrderAfterOrderedPlaces()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Nullable place order", UpdatedAt = DateTime.UtcNow };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Region", DisplayOrder = 1 };
        var expected = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000021"),
            Guid.Parse("00000000-0000-0000-0000-000000000022"),
            Guid.Parse("00000000-0000-0000-0000-000000000023"),
            Guid.Parse("00000000-0000-0000-0000-000000000024"),
            Guid.Parse("00000000-0000-0000-0000-000000000025")
        };
        region.Places.Add(Place(expected[4], user.Id, region, "Later null", null));
        region.Places.Add(Place(expected[2], user.Id, region, "Order gap", 20));
        region.Places.Add(Place(expected[1], user.Id, region, "Later equal", 9));
        region.Places.Add(Place(expected[3], user.Id, region, "Earlier null", null));
        region.Places.Add(Place(expected[0], user.Id, region, "Earlier equal", 9));
        trip.Regions.Add(region);

        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        fixture.RegisterTrip(trip.Id);

        var actual = await new TripEditorPlaceMutationReader(context).LoadPlaceOrderAsync(region.Id, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    private static Place Place(Guid id, string userId, Region region, string name, int? displayOrder) =>
        new() { Id = id, UserId = userId, Region = region, RegionId = region.Id, Name = name, DisplayOrder = displayOrder };
}
