using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies that canonical route refresh remains scoped to the reconciled aggregate and proposal.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentRouteReconcilerTrackerScopePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Successful reconciliation preserves unrelated tracked work for a later caller save.</summary>
    [PostgresFact]
    public async Task Reconcile_Success_PreservesAndPersistsUnrelatedTrackedEntities()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        var unrelatedPlace = await context.Places.SingleAsync(item => item.Id == seeded.UnrelatedPlaceId);
        var unrelatedRegion = await context.Regions.SingleAsync(item => item.Id == seeded.UnrelatedRegionId);
        var unrelatedSegment = await context.Segments.SingleAsync(item => item.Id == seeded.UnrelatedSegmentId);
        var unrelatedTrip = await context.Trips.SingleAsync(item => item.Id == seeded.UnrelatedTripId);
        var originalSegmentMode = unrelatedSegment.Mode;
        var originalTripName = unrelatedTrip.Name;

        var result = await SegmentRouteReconciler.ReconcileAsync(context, ValidProposal(seeded));

        Assert.True(result.Succeeded);
        AssertUnchanged(context, unrelatedPlace, unrelatedRegion, unrelatedSegment, unrelatedTrip);
        Assert.Equal(originalSegmentMode, unrelatedSegment.Mode);
        Assert.Equal(originalTripName, unrelatedTrip.Name);
        unrelatedPlace.Name = "Caller place edit";
        unrelatedRegion.Name = "Caller region edit";
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, context.Entry(unrelatedPlace).State);
        Assert.Equal(EntityState.Modified, context.Entry(unrelatedRegion).State);
        await context.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        Assert.Equal("Caller place edit", await verification.Places
            .Where(item => item.Id == unrelatedPlace.Id).Select(item => item.Name).SingleAsync());
        Assert.Equal("Caller region edit", await verification.Regions
            .Where(item => item.Id == unrelatedRegion.Id).Select(item => item.Name).SingleAsync());
    }

    /// <summary>Validation rejection leaves unrelated tracked entities available for later caller work.</summary>
    [PostgresFact]
    public async Task Reconcile_ValidationFailure_PreservesAndPersistsUnrelatedTrackedEntities()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        var unrelatedPlace = await context.Places.SingleAsync(item => item.Id == seeded.UnrelatedPlaceId);
        var unrelatedRegion = await context.Regions.SingleAsync(item => item.Id == seeded.UnrelatedRegionId);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.TargetSegmentId, seeded.FromPlaceId, seeded.ToPlaceId,
                [new(seeded.FromPlaceId, 0, null)], null));

        Assert.False(result.Succeeded);
        AssertUnchanged(context, unrelatedPlace, unrelatedRegion);
        await PersistAndVerifyUnrelatedEditsAsync(context, unrelatedPlace, unrelatedRegion, "validation");
    }

    /// <summary>A missing target Segment does not disturb unrelated tracked entities or leave a transaction open.</summary>
    [PostgresFact]
    public async Task Reconcile_MissingSegment_PreservesAndPersistsUnrelatedTrackedEntities()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        var unrelatedPlace = await context.Places.SingleAsync(item => item.Id == seeded.UnrelatedPlaceId);
        var unrelatedRegion = await context.Regions.SingleAsync(item => item.Id == seeded.UnrelatedRegionId);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            ValidProposal(seeded) with { SegmentId = Guid.NewGuid() });

        Assert.False(result.Succeeded);
        Assert.Equal(["Segment was not found."], result.Errors);
        Assert.Null(context.Database.CurrentTransaction);
        AssertUnchanged(context, unrelatedPlace, unrelatedRegion);
        await PersistAndVerifyUnrelatedEditsAsync(context, unrelatedPlace, unrelatedRegion, "missing");
    }

    /// <summary>A relevant Place already in the identity map is refreshed before coordinate validation.</summary>
    [PostgresFact]
    public async Task Reconcile_RelevantStalePlace_UsesCanonicalCoordinates()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        _ = await context.Places.SingleAsync(item => item.Id == seeded.WaypointPlaceId);
        await using (var external = fixture.CreateContext())
        {
            var waypoint = await external.Places.SingleAsync(item => item.Id == seeded.WaypointPlaceId);
            waypoint.Location = Point(20, 20);
            await external.SaveChangesAsync();
        }

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.TargetSegmentId, seeded.FromPlaceId, seeded.ToPlaceId,
                [new(seeded.WaypointPlaceId, 0, 1)], Line((1, 1), (2, 2), (3, 3))));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("match", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A relevant Region already in the identity map cannot retain stale Trip ownership authority.</summary>
    [PostgresFact]
    public async Task Reconcile_RelevantStaleRegion_UsesCanonicalTripOwnership()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        _ = await context.Places.Include(item => item.Region)
            .SingleAsync(item => item.Id == seeded.WaypointPlaceId);
        await using (var external = fixture.CreateContext())
        {
            var region = await external.Regions.SingleAsync(item => item.Id == seeded.TargetRegionId);
            region.TripId = seeded.UnrelatedTripId;
            await external.SaveChangesAsync();
        }

        var result = await SegmentRouteReconciler.ReconcileAsync(context, ValidProposal(seeded));

        Assert.False(result.Succeeded);
        Assert.Contains("Every waypoint place must belong to the segment trip.", result.Errors);
    }

    private async Task<SeededScope> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var targetTrip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Target trip" };
        var targetRegion = new Region
        {
            Id = Guid.NewGuid(), Trip = targetTrip, TripId = targetTrip.Id, UserId = user.Id, Name = "Target region"
        };
        var targetPlaces = Enumerable.Range(1, 3).Select(index => new Place
        {
            Id = Guid.NewGuid(), Region = targetRegion, RegionId = targetRegion.Id, UserId = user.Id,
            Name = $"Target {index}", Location = Point(index, index)
        }).ToArray();
        var targetSegment = new Segment
        {
            Id = Guid.NewGuid(), Trip = targetTrip, TripId = targetTrip.Id, UserId = user.Id, Mode = "walk"
        };
        targetTrip.Regions.Add(targetRegion);
        targetTrip.Segments.Add(targetSegment);
        foreach (var place in targetPlaces) targetRegion.Places.Add(place);

        var unrelatedTrip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Unrelated trip" };
        var unrelatedRegion = new Region
        {
            Id = Guid.NewGuid(), Trip = unrelatedTrip, TripId = unrelatedTrip.Id, UserId = user.Id, Name = "Unrelated region"
        };
        var unrelatedPlace = new Place
        {
            Id = Guid.NewGuid(), Region = unrelatedRegion, RegionId = unrelatedRegion.Id, UserId = user.Id,
            Name = "Unrelated place", Location = Point(9, 9)
        };
        var unrelatedSegment = new Segment
        {
            Id = Guid.NewGuid(), Trip = unrelatedTrip, TripId = unrelatedTrip.Id, UserId = user.Id, Mode = "rail"
        };
        unrelatedTrip.Regions.Add(unrelatedRegion);
        unrelatedTrip.Segments.Add(unrelatedSegment);
        unrelatedRegion.Places.Add(unrelatedPlace);

        fixture.RegisterTrip(targetTrip.Id);
        fixture.RegisterTrip(unrelatedTrip.Id);
        await using var seed = fixture.CreateContext();
        seed.Trips.AddRange(targetTrip, unrelatedTrip);
        await seed.SaveChangesAsync();
        return new(targetTrip.Id, targetRegion.Id, targetSegment.Id, targetPlaces[0].Id, targetPlaces[1].Id,
            targetPlaces[2].Id, unrelatedTrip.Id, unrelatedRegion.Id, unrelatedPlace.Id, unrelatedSegment.Id);
    }

    private static SegmentRouteProposal ValidProposal(SeededScope seeded) =>
        new(seeded.TargetSegmentId, seeded.FromPlaceId, seeded.ToPlaceId,
            [new(seeded.WaypointPlaceId, 0, null)], null);

    private async Task PersistAndVerifyUnrelatedEditsAsync(
        ApplicationDbContext context,
        Place place,
        Region region,
        string suffix)
    {
        place.Name = $"Place after {suffix}";
        region.Name = $"Region after {suffix}";
        await context.SaveChangesAsync();
        await using var verification = fixture.CreateContext();
        Assert.Equal(place.Name, await verification.Places.Where(item => item.Id == place.Id)
            .Select(item => item.Name).SingleAsync());
        Assert.Equal(region.Name, await verification.Regions.Where(item => item.Id == region.Id)
            .Select(item => item.Name).SingleAsync());
    }

    private static void AssertUnchanged(ApplicationDbContext context, params object[] entities)
    {
        foreach (var entity in entities) Assert.Equal(EntityState.Unchanged, context.Entry(entity).State);
    }

    private static Point Point(double x, double y) => new(x, y) { SRID = 4326 };

    private static LineString Line(params (double X, double Y)[] points) =>
        new(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()) { SRID = 4326 };

    private sealed record SeededScope(
        Guid TargetTripId,
        Guid TargetRegionId,
        Guid TargetSegmentId,
        Guid FromPlaceId,
        Guid WaypointPlaceId,
        Guid ToPlaceId,
        Guid UnrelatedTripId,
        Guid UnrelatedRegionId,
        Guid UnrelatedPlaceId,
        Guid UnrelatedSegmentId);
}
