using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies canonical loading, validation, and defensive route ownership.</summary>
public sealed class SegmentRouteReconcilerTests
{
    /// <summary>Legacy zero-waypoint segments retain optional endpoints and receive a defensive geometry copy.</summary>
    [Fact]
    public async Task ReconcileAsync_ZeroWaypoints_PreservesLegacyCompatibility()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var geometry = Line((1, 1), (2, 2));

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, null, null, [], geometry));

        Assert.True(result.Succeeded);
        var segment = await context.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(segment.FromPlaceId);
        Assert.Null(segment.ToPlaceId);
        Assert.NotSame(geometry, segment.RouteGeometry);
        Assert.Empty(segment.Waypoints);
    }

    /// <summary>A valid fallback proposal commits canonical endpoints and deterministic waypoint order.</summary>
    [Fact]
    public async Task ReconcileAsync_ValidFallback_UsesCanonicalAnchorChain()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(seeded.PlaceIds[1], 0, null)], null));

        Assert.True(result.Succeeded);
        Assert.Equal(seeded.PlaceIds, result.EffectiveAnchorChain.Select(item => item.Id));
        var waypoint = await context.Set<SegmentWaypoint>().SingleAsync();
        Assert.Equal(seeded.PlaceIds[1], waypoint.PlaceId);
        Assert.Equal(0, waypoint.Position);
        Assert.Null(waypoint.RouteVertexIndex);
    }

    /// <summary>Canonical endpoint identity may be reused for an approved closed loop.</summary>
    [Fact]
    public async Task ReconcileAsync_ClosedLoop_UsesOneCanonicalEndpoint()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, seeded.PlaceIds[0], seeded.PlaceIds[0],
                [new(seeded.PlaceIds[1], 0, null)], null));

        Assert.True(result.Succeeded);
        Assert.Equal(seeded.PlaceIds[0], result.EffectiveAnchorChain[0].Id);
        Assert.Equal(seeded.PlaceIds[0], result.EffectiveAnchorChain[^1].Id);
    }

    /// <summary>Custom geometry is validated and stored as the same defensive copy.</summary>
    [Fact]
    public async Task ReconcileAsync_CustomGeometry_StoresValidatedDefensiveCopy()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var geometry = Line((1, 1), (2, 2), (3, 3));

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(seeded.PlaceIds[1], 0, 1)], geometry));
        var segment = await context.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        var storedCopy = segment.RouteGeometry;
        geometry.GetCoordinateN(1).X = 99;
        geometry.SRID = 3857;

        Assert.True(result.Succeeded);
        Assert.NotSame(geometry, storedCopy);
        Assert.Equal(2, storedCopy!.GetCoordinateN(1).X);
        Assert.Equal(4326, storedCopy.SRID);
    }

    /// <summary>Caller-created Place graphs cannot enter the identity-only proposal boundary.</summary>
    [Fact]
    public async Task ReconcileAsync_FabricatedDetachedPlace_CannotInfluenceCanonicalState()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var fabricated = new Place
        {
            Id = seeded.PlaceIds[1],
            Region = new Region { TripId = seeded.TripId },
            Location = new Point(99, 99) { SRID = 4326 }
        };

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(fabricated.Id, 0, 1)], Line((1, 1), (99, 99), (3, 3))));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(context.ChangeTracker.Entries<Place>(), entry => ReferenceEquals(entry.Entity, fabricated));
        Assert.Empty(await context.Set<SegmentWaypoint>().ToListAsync());
    }

    /// <summary>Canonical coordinates, rather than stale caller assumptions, authorize a custom route.</summary>
    [Fact]
    public async Task ReconcileAsync_CanonicalLocation_IsUsedForValidation()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(seeded.PlaceIds[1], 0, 1)], Line((1, 1), (2, 2), (3, 3))));

        Assert.True(result.Succeeded);
    }

    /// <summary>Missing endpoint and waypoint identities produce deterministic distinct errors.</summary>
    [Fact]
    public async Task ReconcileAsync_MissingIdentities_AreDistinguished()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var missingEndpoint = Guid.NewGuid();
        var missingWaypoint = Guid.NewGuid();

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, missingEndpoint, seeded.PlaceIds[2], [new(missingWaypoint, 0, null)], null));

        Assert.False(result.Succeeded);
        Assert.Contains("From place was not found.", result.Errors);
        Assert.Contains("Waypoint place at position 0 was not found.", result.Errors);
    }

    /// <summary>Canonical cross-trip endpoint and waypoint ownership failures are distinguished by label.</summary>
    [Fact]
    public async Task ReconcileAsync_CrossTripIdentities_AreRejected()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context, includeForeign: true);

        var endpoint = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, seeded.ForeignPlaceId, seeded.PlaceIds[2], [new(seeded.PlaceIds[1], 0, null)], null));
        var waypoint = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, seeded.PlaceIds[0], seeded.PlaceIds[2], [new(seeded.ForeignPlaceId!.Value, 0, null)], null));

        Assert.Contains("From place must belong to the segment trip.", endpoint.Errors);
        Assert.Contains("Every waypoint place must belong to the segment trip.", waypoint.Errors);
    }

    /// <summary>Representative malformed proposals leave tracked and persisted aggregate state unchanged.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task ReconcileAsync_InvalidProposal_IsAtomic(int position, bool duplicateEndpoint)
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var original = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(seeded.PlaceIds[1], 0, null)], null));
        Assert.True(original.Succeeded);
        var before = await SnapshotAsync(context, seeded.SegmentId);
        var waypointId = duplicateEndpoint ? seeded.PlaceIds[0] : seeded.PlaceIds[1];

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [new(waypointId, position, null)], null));

        Assert.False(result.Succeeded);
        Assert.Equal(before, await SnapshotAsync(context, seeded.SegmentId));
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    /// <summary>The inclusive seven-decimal coordinate tolerance remains accepted.</summary>
    [Fact]
    public async Task ReconcileAsync_CoordinateAtToleranceBoundary_IsAccepted()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await SegmentRouteReconciler.ReconcileAsync(context, Proposal(seeded,
            [new(seeded.PlaceIds[1], 0, 1)],
            Line((1.0000001, 0.9999999), (2.0000001, 1.9999999), (3.0000001, 2.9999999))));

        Assert.True(result.Succeeded);
    }

    /// <summary>Automatic reconciliation derives distance and duration from the complete canonical fallback route.</summary>
    [Fact]
    public async Task ReconcileAsync_AutomaticUsesUnroundedCanonicalRouteAndProfileSpeed()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [], null, new("walk", seeded.ProfileId, EstimatedDurationSource.Automatic, null)));

        var segment = await context.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        var distance = SegmentMeasurementCalculator.CalculateDistance([
            new Coordinate(1, 1), new Coordinate(3, 3)]);
        Assert.True(result.Succeeded);
        Assert.Equal(distance.RoundedKilometres, segment.EstimatedDistanceKm);
        Assert.Equal(SegmentMeasurementCalculator.CalculateAutomaticDuration(distance.UnroundedMetres, 5), segment.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
    }

    /// <summary>Manual reconciliation normalizes duration while distance remains server-derived.</summary>
    [Fact]
    public async Task ReconcileAsync_ManualPreservesExplicitDurationButRejectsNull()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var valid = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [], null, new("walk", seeded.ProfileId, EstimatedDurationSource.Manual, 0.025)));

        Assert.True(valid.Succeeded);
        var segment = await context.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Manual, segment.EstimatedDurationSource);

        var invalid = await SegmentRouteReconciler.ReconcileAsync(context,
            Proposal(seeded, [], null, new("walk", seeded.ProfileId, EstimatedDurationSource.Manual, null)));
        Assert.False(invalid.Succeeded);
        Assert.Contains("Manual duration is required.", invalid.Errors);
    }

    /// <summary>Incomplete fallback clears Automatic measurements while a newly requested missing speed is rejected.</summary>
    [Fact]
    public async Task ReconcileAsync_AutomaticDistinguishesUnavailableRouteFromUnavailableSpeed()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);
        var incomplete = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, null, null, [], null,
                new("walk", seeded.ProfileId, EstimatedDurationSource.Automatic, null)));

        Assert.True(incomplete.Succeeded);
        var segment = await context.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(segment.EstimatedDistanceKm);
        Assert.Null(segment.EstimatedDuration);

        var unavailable = await SegmentRouteReconciler.ReconcileAsync(context,
            new(seeded.SegmentId, null, null, [], null,
                new(string.Empty, null, EstimatedDurationSource.Automatic, null)));
        Assert.False(unavailable.Succeeded);
        Assert.Contains("Automatic duration requires a linked profile with a positive planning speed.", unavailable.Errors);
    }

    private static ApplicationDbContext CreateContext()
    {
        var services = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new(options, services);
    }

    private static async Task<SeededAggregate> SeedAsync(ApplicationDbContext context, bool includeForeign = false)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = Guid.NewGuid().ToString(), DisplayName = "owner" };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = "trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "region" };
        var places = Enumerable.Range(1, 3).Select(index => new Place
        {
            Id = Guid.NewGuid(), RegionId = region.Id, Region = region, UserId = user.Id,
            Name = $"place {index}", Location = new Point(index, index) { SRID = 4326 }
        }).ToArray();
        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = "walk", Label = "Walk", Category = "Land",
            PlanningSpeedKmh = 5, IsActive = true
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Mode = "walk",
            TransportProfileId = profile.Id, TransportProfile = profile
        };
        context.AddRange(user, trip, region, profile, segment);
        context.Places.AddRange(places);
        Guid? foreignPlaceId = null;
        if (includeForeign)
        {
            var foreignTrip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = "foreign" };
            var foreignRegion = new Region { Id = Guid.NewGuid(), TripId = foreignTrip.Id, Trip = foreignTrip, UserId = user.Id, Name = "foreign" };
            var foreign = new Place { Id = Guid.NewGuid(), RegionId = foreignRegion.Id, Region = foreignRegion, UserId = user.Id, Name = "foreign", Location = new Point(9, 9) { SRID = 4326 } };
            context.AddRange(foreignTrip, foreignRegion, foreign);
            foreignPlaceId = foreign.Id;
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new(segment.Id, trip.Id, places.Select(item => item.Id).ToArray(), profile.Id, foreignPlaceId);
    }

    private static SegmentRouteProposal Proposal(
        SeededAggregate seeded,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        LineString? geometry,
        SegmentMeasurementProposal? measurement = null) =>
        new(seeded.SegmentId, seeded.PlaceIds[0], seeded.PlaceIds[2], waypoints, geometry, measurement);

    private static LineString Line(params (double X, double Y)[] points) =>
        new(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()) { SRID = 4326 };

    private static async Task<string> SnapshotAsync(ApplicationDbContext context, Guid segmentId)
    {
        var segment = await SegmentRouteReconciler.LoadAggregateAsync(context, segmentId);
        return $"{segment!.FromPlaceId}|{segment.ToPlaceId}|{segment.RouteGeometry?.AsText()}|{string.Join(';', segment.Waypoints.OrderBy(item => item.Position).Select(item => $"{item.PlaceId}:{item.Position}:{item.RouteVertexIndex}"))}";
    }

    private sealed record SeededAggregate(Guid SegmentId, Guid TripId, Guid[] PlaceIds, Guid ProfileId, Guid? ForeignPlaceId);
}
