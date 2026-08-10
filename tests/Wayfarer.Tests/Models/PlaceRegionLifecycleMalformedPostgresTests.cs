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
    /// <summary>Rejects Region deletion when a surviving custom route or waypoint mapping is malformed.</summary>
    [PostgresTheory]
    [InlineData(MalformedRegionState.Geometry)]
    [InlineData(MalformedRegionState.Position)]
    [InlineData(MalformedRegionState.RouteVertexIndex)]
    public async Task RegionDeletion_MalformedSurvivingState_RejectsWithoutMutation(
        MalformedRegionState malformedState)
    {
        var seeded = await SeedMalformedRegionAsync(malformedState);
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        await using var context = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(context, confirmation);
        var challenge = await service.DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteRegionAsync(
            seeded.TripId,
            seeded.DeletedRegionId,
            seeded.UserId,
            challenge.Warning!.ConfirmationToken,
            CancellationToken.None));

        await using var verification = fixture.CreateContext();
        Assert.True(await verification.Regions.AnyAsync(item => item.Id == seeded.DeletedRegionId));
        Assert.True(await verification.Places.AnyAsync(item => item.Id == seeded.DeletedPlaceId));
        Assert.True(await verification.Segments.AnyAsync(item => item.Id == seeded.SegmentId));
        Assert.Single(await verification.Set<SegmentWaypoint>()
            .Where(item => item.SegmentId == seeded.SegmentId).ToArrayAsync());
    }

    /// <summary>Deletes each waypoint position while preserving anonymous vertices and exact surviving indices.</summary>
    [PostgresTheory]
    [InlineData(0, new[] { 0, 1 }, new[] { 4, 6 })]
    [InlineData(1, new[] { 0, 1 }, new[] { 2, 6 })]
    [InlineData(2, new[] { 0, 1 }, new[] { 2, 4 })]
    public async Task PlaceDeletion_FirstMiddleLastWaypoint_PreservesExactSurvivorState(
        int deletedPosition,
        int[] expectedPositions,
        int[] expectedIndices)
    {
        var seeded = await SeedWaypointSetAsync([0, 1, 2], deletedPosition);
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        await using var context = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(context, confirmation);
        var challenge = await service.DeletePlaceAsync(
            seeded.TripId, seeded.DeletedPlaceId, seeded.UserId, null, CancellationToken.None);

        var result = await service.DeletePlaceAsync(
            seeded.TripId,
            seeded.DeletedPlaceId,
            seeded.UserId,
            challenge.Warning!.ConfirmationToken,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = fixture.CreateContext();
        var segment = await verification.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SegmentId);
        var waypoints = await verification.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => item.SegmentId == seeded.SegmentId)
            .OrderBy(item => item.Position)
            .ToArrayAsync();
        Assert.Equal(expectedPositions, waypoints.Select(item => item.Position));
        Assert.Equal(expectedIndices, waypoints.Select(item => item.RouteVertexIndex!.Value));
        Assert.Equal(seeded.DeletedCoordinate, segment.RouteGeometry!.Coordinates[2 + (deletedPosition * 2)]);
    }

    /// <summary>Rejects deletion when a surviving waypoint has a non-contiguous canonical Position.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_MalformedSurvivingWaypointPosition_RejectsWithoutMutation()
    {
        var seeded = await SeedWaypointSetAsync([0, 2], deletedPosition: 0);
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

    private async Task<MalformedSeed> SeedWaypointSetAsync(int[] positions, int deletedPosition)
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
        var waypointPlaces = new[]
        {
            Place(region, user.Id, "First", 2, 2),
            Place(region, user.Id, "Middle", 3, 3),
            Place(region, user.Id, "Last", 4, 4)
        }.Take(positions.Length).ToArray();
        var deleted = waypointPlaces[deletedPosition];
        var to = Place(region, user.Id, "To", 5, 5);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([
                new(1, 1), new(1.5, 1.5), new(2, 2), new(2.5, 2.5), new(3, 3),
                new(3.5, 3.5), new(4, 4), new(4.5, 4.5), new(5, 5)]) { SRID = 4326 }
        };
        for (var index = 0; index < waypointPlaces.Length; index++)
            segment.Waypoints.Add(Waypoint(segment, waypointPlaces[index], positions[index], 2 + (index * 2)));
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, deleted.Id, segment.Id, deleted.Location!.Coordinate.Copy());
    }

    private async Task<MalformedRegionSeed> SeedMalformedRegionAsync(MalformedRegionState malformedState)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Malformed Region lifecycle" };
        fixture.RegisterTrip(trip.Id);
        var deletedRegion = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Deleted", DisplayOrder = 1
        };
        var outsideRegion = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Outside", DisplayOrder = 2
        };
        trip.Regions.Add(deletedRegion);
        trip.Regions.Add(outsideRegion);
        var deleted = Place(deletedRegion, user.Id, "Deleted waypoint", 2, 2);
        var from = Place(outsideRegion, user.Id, "From", 1, 1);
        var to = Place(outsideRegion, user.Id, "To", 3, 3);
        var geometry = malformedState == MalformedRegionState.Geometry
            ? new LineString([new(1, 1), new(3, 3)]) { SRID = 4326 }
            : new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1, RouteGeometry = geometry
        };
        segment.Waypoints.Add(Waypoint(
            segment,
            deleted,
            malformedState == MalformedRegionState.Position ? 2 : 0,
            malformedState == MalformedRegionState.RouteVertexIndex ? 99 : 1));
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, deletedRegion.Id, deleted.Id, segment.Id);
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

    private sealed record MalformedSeed(
        string UserId,
        Guid TripId,
        Guid DeletedPlaceId,
        Guid SegmentId,
        Coordinate DeletedCoordinate);

    private sealed record MalformedRegionSeed(
        string UserId,
        Guid TripId,
        Guid DeletedRegionId,
        Guid DeletedPlaceId,
        Guid SegmentId);

    public enum MalformedRegionState { Geometry, Position, RouteVertexIndex }
}
