using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes distinct empty, child-only, and overlapping Region deletion transitions.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleRegionMatrixPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Deletes an empty Region without confirmation and normalizes authoritative Region order.</summary>
    [PostgresFact]
    public async Task EmptyRegion_DeletesWithoutConfirmationAndNormalizesRegionOrder()
    {
        var seeded = await SeedRegionsAsync(includeChild: false);
        await using var context = fixture.CreateContext();

        var result = await Service(context).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Warning);
        Assert.Equal(seeded.DeletedRegionId, result.RegionId);
        Assert.Empty(result.PlaceIds);
        Assert.Empty(result.AreaIds);
        Assert.Empty(result.SegmentIds);
        await using var verification = fixture.CreateContext();
        var regionOrders = await verification.Regions.AsNoTracking()
            .Where(item => item.TripId == seeded.TripId)
            .OrderBy(item => item.DisplayOrder).Select(item => item.DisplayOrder).ToArrayAsync();
        Assert.Equal(new[] { 0, 1, 2 }, regionOrders);
    }

    /// <summary>Requires confirmation for a child-only Region and returns its exact child identity.</summary>
    [PostgresFact]
    public async Task ChildOnlyRegion_RequiresConfirmationAndDeletesExactChild()
    {
        var seeded = await SeedRegionsAsync(includeChild: true);
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        await using var context = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(context, confirmation);

        var challenge = await service.DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);
        Assert.False(challenge.Succeeded);
        Assert.Equal("region-delete-dependencies", challenge.Warning!.Code);
        Assert.Equal(1, challenge.Warning.DeletedPlaces.Count);
        Assert.True(await context.Places.AnyAsync(item => item.Id == seeded.ChildPlaceId));

        var result = await service.DeleteRegionAsync(
            seeded.TripId,
            seeded.DeletedRegionId,
            seeded.UserId,
            challenge.Warning.ConfirmationToken,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([seeded.ChildPlaceId!.Value], result.PlaceIds);
        Assert.Empty(result.SegmentIds);
    }

    /// <summary>Deduplicates mixed dependencies and returns a complete no-reload destructive envelope.</summary>
    [PostgresFact]
    public async Task MixedOverlappingRegionDeletion_ReturnsExactIdsOrdersAndSurvivingRoutes()
    {
        var seeded = await SeedMixedAsync();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        await using var context = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(context, confirmation);
        var challenge = await service.DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);

        var result = await service.DeleteRegionAsync(
            seeded.TripId,
            seeded.DeletedRegionId,
            seeded.UserId,
            challenge.Warning!.ConfirmationToken,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(seeded.DeletedPlaceIds.Order(), result.PlaceIds);
        Assert.Equal([seeded.AreaId], result.AreaIds);
        Assert.Equal(seeded.DeletedSegmentIds.Order(), result.SegmentIds);
        Assert.Equal(seeded.SurvivingSegmentIds.Order(), result.SurvivingSegments.Select(item => item.Id));
        var customResult = result.SurvivingSegments.Single(item => item.Id == seeded.CustomSurvivorId);
        var fallbackResult = result.SurvivingSegments.Single(item => item.Id == seeded.FallbackSurvivorId);
        Assert.Equal(seeded.CustomCoordinates, customResult.RouteGeometry!.Coordinates);
        Assert.Empty(customResult.Waypoints);
        Assert.Null(fallbackResult.RouteGeometry);
        Assert.Empty(fallbackResult.Waypoints);
        Assert.NotNull(customResult.EstimatedDistanceKm);

        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Regions.AnyAsync(item => item.Id == seeded.DeletedRegionId));
        Assert.False(await verification.Places.AnyAsync(item => seeded.DeletedPlaceIds.Contains(item.Id)));
        Assert.False(await verification.Areas.AnyAsync(item => item.Id == seeded.AreaId));
        Assert.False(await verification.Segments.AnyAsync(item => seeded.DeletedSegmentIds.Contains(item.Id)));
        var regionOrders = await verification.Regions.AsNoTracking()
            .Where(item => item.TripId == seeded.TripId)
            .OrderBy(item => item.DisplayOrder).Select(item => item.DisplayOrder).ToArrayAsync();
        var segmentOrders = await verification.Segments.AsNoTracking()
            .Where(item => item.TripId == seeded.TripId)
            .OrderBy(item => item.DisplayOrder).Select(item => item.DisplayOrder).ToArrayAsync();
        Assert.Equal(new[] { 0, 1, 2 }, regionOrders);
        Assert.Equal(new[] { 1, 2 }, segmentOrders);
    }

    private async Task<RegionSeed> SeedRegionsAsync(bool includeChild)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Region matrix" };
        fixture.RegisterTrip(trip.Id);
        Region(trip, user.Id, "Unassigned Places", 0);
        Region(trip, user.Id, "Before", 2);
        var deleted = Region(trip, user.Id, "Deleted", 3);
        Region(trip, user.Id, "After", 4);
        var child = includeChild ? Place(deleted, user.Id, "Child", 1, 1) : null;
        await SaveAsync(trip);
        return new(user.Id, trip.Id, deleted.Id, child?.Id);
    }

    private async Task<MixedSeed> SeedMixedAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Mixed Region matrix" };
        fixture.RegisterTrip(trip.Id);
        Region(trip, user.Id, "Unassigned Places", 0);
        var outside = Region(trip, user.Id, "Outside", 2);
        var deleted = Region(trip, user.Id, "Deleted", 3);
        Region(trip, user.Id, "After", 4);
        var first = Place(deleted, user.Id, "First deleted", 2, 2);
        var second = Place(deleted, user.Id, "Second deleted", 3, 3);
        var from = Place(outside, user.Id, "From", 1, 1);
        var to = Place(outside, user.Id, "To", 5, 5);
        var area = new Area
        {
            Id = Guid.NewGuid(), Region = deleted, RegionId = deleted.Id,
            Name = "Deleted area", DisplayOrder = 1,
            Geometry = new Polygon(new LinearRing([
                new(0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0)])) { SRID = 4326 }
        };
        deleted.Areas.Add(area);
        var coordinates = new[]
        {
            new Coordinate(1, 1), new(1.5, 1.5), new(2, 2),
            new(2.5, 2.5), new(3, 3), new(4, 4), new(5, 5)
        };
        var custom = Segment(trip, user.Id, from, to, 4, new LineString(coordinates) { SRID = 4326 });
        AddWaypoint(custom, first, 0, 2);
        AddWaypoint(custom, second, 1, 4);
        var fallback = Segment(trip, user.Id, from, to, 7, null);
        AddWaypoint(fallback, first, 0, null);
        var closedLoop = Segment(trip, user.Id, first, first, 2,
            new LineString([new(2, 2), new(2.5, 2.5), new(2, 2)]) { SRID = 4326 });
        var endpoint = Segment(trip, user.Id, second, to, 9,
            new LineString([new(3, 3), new(4, 4), new(5, 5)]) { SRID = 4326 });
        await SaveAsync(trip);
        return new(
            user.Id,
            trip.Id,
            deleted.Id,
            [first.Id, second.Id],
            area.Id,
            [closedLoop.Id, endpoint.Id],
            [custom.Id, fallback.Id],
            custom.Id,
            fallback.Id,
            coordinates);
    }

    private async Task SaveAsync(Trip trip)
    {
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
    }

    private static Segment Segment(
        Trip trip,
        string userId,
        Place from,
        Place to,
        int displayOrder,
        LineString? geometry)
    {
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = displayOrder,
            RouteGeometry = geometry
        };
        trip.Segments.Add(segment);
        return segment;
    }

    private static void AddWaypoint(Segment segment, Place place, int position, int? index) =>
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = place, PlaceId = place.Id,
            Position = position, RouteVertexIndex = index
        });

    private static Region Region(Trip trip, string userId, string name, int displayOrder)
    {
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId,
            Name = name, DisplayOrder = displayOrder
        };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1,
            Location = new Point(x, y) { SRID = 4326 }
        };
        region.Places.Add(place);
        return place;
    }

    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) =>
        new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

    private sealed record RegionSeed(string UserId, Guid TripId, Guid DeletedRegionId, Guid? ChildPlaceId);

    private sealed record MixedSeed(
        string UserId,
        Guid TripId,
        Guid DeletedRegionId,
        Guid[] DeletedPlaceIds,
        Guid AreaId,
        Guid[] DeletedSegmentIds,
        Guid[] SurvivingSegmentIds,
        Guid CustomSurvivorId,
        Guid FallbackSurvivorId,
        Coordinate[] CustomCoordinates);
}
