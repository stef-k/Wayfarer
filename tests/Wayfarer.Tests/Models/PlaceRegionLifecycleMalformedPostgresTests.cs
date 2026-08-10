using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes malformed canonical lifecycle states that must fail without mutation.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleMalformedPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Rejects deletion when a surviving waypoint has a non-contiguous canonical Position.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_MalformedSurvivingWaypointPosition_RejectsWithoutMutation()
    {
        var seeded = await SeedMalformedPositionAsync();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        await using var context = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(context, confirmation);
        var challenge = await service.DeletePlaceAsync(
            seeded.TripId, seeded.DeletedPlaceId, seeded.UserId, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeletePlaceAsync(
            seeded.TripId,
            seeded.DeletedPlaceId,
            seeded.UserId,
            challenge.Warning!.ConfirmationToken,
            CancellationToken.None));

        await using var verification = fixture.CreateContext();
        Assert.True(await verification.Places.AnyAsync(item => item.Id == seeded.DeletedPlaceId));
        var waypoints = await verification.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => item.SegmentId == seeded.SegmentId)
            .OrderBy(item => item.Position)
            .ToArrayAsync();
        Assert.Equal([0, 2], waypoints.Select(item => item.Position));
        Assert.Equal([2, 4], waypoints.Select(item => item.RouteVertexIndex));
    }

    private async Task<MalformedSeed> SeedMalformedPositionAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Malformed lifecycle" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Region", DisplayOrder = 1
        };
        trip.Regions.Add(region);
        var from = Place(region, user.Id, "From", 1, 1);
        var deleted = Place(region, user.Id, "Deleted", 2, 2);
        var survivor = Place(region, user.Id, "Survivor", 3, 3);
        var to = Place(region, user.Id, "To", 5, 5);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([
                new(1, 1), new(1.5, 1.5), new(2, 2), new(2.5, 2.5), new(3, 3), new(5, 5)]) { SRID = 4326 }
        };
        segment.Waypoints.Add(Waypoint(segment, deleted, 0, 2));
        segment.Waypoints.Add(Waypoint(segment, survivor, 2, 4));
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, deleted.Id, segment.Id);
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1, Location = new Point(x, y) { SRID = 4326 }
        };
        region.Places.Add(place);
        return place;
    }

    private static SegmentWaypoint Waypoint(Segment segment, Place place, int position, int routeVertexIndex) => new()
    {
        Segment = segment,
        SegmentId = segment.Id,
        Place = place,
        PlaceId = place.Id,
        Position = position,
        RouteVertexIndex = routeVertexIndex
    };

    private sealed record MalformedSeed(string UserId, Guid TripId, Guid DeletedPlaceId, Guid SegmentId);
}
