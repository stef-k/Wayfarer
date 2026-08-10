using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes waypoint-aware Place lifecycle mutation against PostgreSQL/PostGIS.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecyclePostgresTests
{
    private readonly PostgresImportTestFixture _fixture;

    /// <summary>Initializes lifecycle provider tests over the guarded isolated database.</summary>
    public PlaceRegionLifecyclePostgresTests(PostgresImportTestFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task WaypointMovementPreservesAnonymousVerticesAndReconcilesMeasurementsAtomically()
    {
        var seeded = await SeedAsync(customRoute: true);
        await using var context = _fixture.CreateContext();
        var service = Service(context);

        var result = await service.UpdatePlaceAsync(
            seeded.TripId,
            seeded.WaypointId,
            seeded.UserId,
            new(seeded.WaypointRegionId, "Waypoint", "", "", "marker", "bg-blue", Point(4, 4)),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = _fixture.CreateContext();
        var segment = await verification.Segments.Include(item => item.Waypoints).SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(new Coordinate(1.5, 1.5), segment.RouteGeometry!.Coordinates[1]);
        Assert.Equal(new Coordinate(4, 4), segment.RouteGeometry.Coordinates[2]);
        Assert.Equal(new Coordinate(2.5, 2.5), segment.RouteGeometry.Coordinates[3]);
        Assert.Equal(2, Assert.Single(segment.Waypoints).RouteVertexIndex);
        Assert.NotNull(segment.EstimatedDistanceKm);
    }

    [PostgresFact]
    public async Task ConfirmedWaypointDeletionRetainsFormerAnchorVertexAndFallbackRemainsNull()
    {
        foreach (var customRoute in new[] { true, false })
        {
            var seeded = await SeedAsync(customRoute);
            await using var context = _fixture.CreateContext();
            var service = Service(context);
            var challenge = await service.DeletePlaceAsync(seeded.TripId, seeded.WaypointId, seeded.UserId, null, CancellationToken.None);
            Assert.NotNull(challenge.Warning);

            var result = await service.DeletePlaceAsync(
                seeded.TripId, seeded.WaypointId, seeded.UserId, challenge.Warning!.ConfirmationToken, CancellationToken.None);

            Assert.True(result.Succeeded);
            await using var verification = _fixture.CreateContext();
            var segment = await verification.Segments.Include(item => item.Waypoints).SingleAsync(item => item.Id == seeded.SegmentId);
            Assert.Empty(segment.Waypoints);
            if (customRoute)
            {
                Assert.Equal(5, segment.RouteGeometry!.NumPoints);
                Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry.Coordinates[2]);
            }
            else
            {
                Assert.Null(segment.RouteGeometry);
            }
        }
    }

    private async Task<SeededLifecycle> SeedAsync(bool customRoute)
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Lifecycle fixture", UpdatedAt = DateTime.UtcNow };
        _fixture.RegisterTrip(trip.Id);
        var fromRegion = Region(trip, user.Id, "From", 1);
        var waypointRegion = Region(trip, user.Id, "Waypoint", 2);
        var toRegion = Region(trip, user.Id, "To", 3);
        var from = Place(fromRegion, user.Id, "From", Point(1, 1));
        var waypoint = Place(waypointRegion, user.Id, "Waypoint", Point(2, 2));
        var to = Place(toRegion, user.Id, "To", Point(3, 3));
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = customRoute ? new LineString([new(1, 1), new(1.5, 1.5), new(2, 2), new(2.5, 2.5), new(3, 3)]) { SRID = 4326 } : null
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
            Position = 0, RouteVertexIndex = customRoute ? 2 : null
        });
        trip.Segments.Add(segment);
        await using var context = _fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, waypointRegion.Id, waypoint.Id, segment.Id);
    }

    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) =>
        new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

    private static Region Region(Trip trip, string userId, string name, int order)
    {
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = name, DisplayOrder = order };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, Point location)
    {
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = name, DisplayOrder = 1, Location = location };
        region.Places.Add(place);
        return place;
    }

    private static Point Point(double x, double y) => new(x, y) { SRID = 4326 };

    private sealed record SeededLifecycle(string UserId, Guid TripId, Guid WaypointRegionId, Guid WaypointId, Guid SegmentId);
}
