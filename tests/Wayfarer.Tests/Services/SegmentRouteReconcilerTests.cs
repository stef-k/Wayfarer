using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies the server-authoritative segment route aggregate invariants.</summary>
public sealed class SegmentRouteReconcilerTests
{
    /// <summary>Legacy zero-waypoint segments retain optional endpoints and geometry.</summary>
    [Fact]
    public void Reconcile_ZeroWaypoints_PreservesLegacyCompatibility()
    {
        var segment = Segment();
        var geometry = Line(Coordinate(1, 1), Coordinate(2, 2));

        var result = SegmentRouteReconciler.Reconcile(segment, null, null, [], geometry);

        Assert.True(result.Succeeded);
        Assert.Null(segment.FromPlaceId);
        Assert.Null(segment.ToPlaceId);
        Assert.Same(geometry, segment.RouteGeometry);
        Assert.Empty(segment.Waypoints);
    }

    /// <summary>A valid fallback proposal retains null geometry and produces its deterministic anchor chain.</summary>
    [Fact]
    public void Reconcile_ValidFallback_ProducesOrderedAnchorChain()
    {
        var tripId = Guid.NewGuid();
        var from = Place(tripId, 1, 1);
        var via = Place(tripId, 2, 2);
        var to = Place(tripId, 3, 3);
        var segment = Segment(tripId);

        var result = SegmentRouteReconciler.Reconcile(segment, from, to, [new(via, 0, null)], null);

        Assert.True(result.Succeeded);
        Assert.Null(segment.RouteGeometry);
        Assert.Collection(segment.Waypoints, waypoint =>
        {
            Assert.Equal(via.Id, waypoint.PlaceId);
            Assert.Equal(0, waypoint.Position);
            Assert.Null(waypoint.RouteVertexIndex);
        });
        Assert.Equal([from.Location!.Coordinate, via.Location!.Coordinate, to.Location!.Coordinate],
            result.EffectiveAnchorChain.Select(place => place.Location!.Coordinate), CoordinateComparer.Instance);
    }

    /// <summary>A closed loop reuses one canonical endpoint place without duplicating it.</summary>
    [Fact]
    public void Reconcile_ValidClosedLoop_UsesCanonicalEndpointPlace()
    {
        var tripId = Guid.NewGuid();
        var endpoint = Place(tripId, 1, 1);
        var via = Place(tripId, 2, 2);
        var segment = Segment(tripId);

        var result = SegmentRouteReconciler.Reconcile(segment, endpoint, endpoint, [new(via, 0, null)], null);

        Assert.True(result.Succeeded);
        Assert.Equal(endpoint.Id, segment.FromPlaceId);
        Assert.Equal(endpoint.Id, segment.ToPlaceId);
        Assert.Equal(endpoint.Id, result.EffectiveAnchorChain[0].Id);
        Assert.Equal(endpoint.Id, result.EffectiveAnchorChain[^1].Id);
    }

    /// <summary>A custom route accepts complete ordered interior anchor mappings.</summary>
    [Fact]
    public void Reconcile_ValidCustomGeometry_AcceptsOrderedWaypointIndices()
    {
        var tripId = Guid.NewGuid();
        var from = Place(tripId, 1, 1);
        var first = Place(tripId, 2, 2);
        var second = Place(tripId, 4, 4);
        var to = Place(tripId, 5, 5);
        var geometry = Line(Coordinate(1, 1), Coordinate(1.5, 1.5), Coordinate(2, 2), Coordinate(3, 3), Coordinate(4, 4), Coordinate(5, 5));
        var segment = Segment(tripId);

        var result = SegmentRouteReconciler.Reconcile(segment, from, to, [new(first, 0, 2), new(second, 1, 4)], geometry);

        Assert.True(result.Succeeded);
        Assert.Same(geometry, segment.RouteGeometry);
        Assert.Equal([2, 4], segment.Waypoints.Select(waypoint => waypoint.RouteVertexIndex));
    }

    /// <summary>Representative invalid proposals reject without changing tracked aggregate state.</summary>
    [Theory]
    [MemberData(nameof(InvalidProposals))]
    public void Reconcile_InvalidProposal_LeavesAggregateUnchanged(
        Func<Guid, Place, Place, Place, (Place?, Place?, SegmentWaypointProposal[], LineString?)> proposal)
    {
        var tripId = Guid.NewGuid();
        var originalFrom = Place(tripId, 10, 10);
        var originalTo = Place(tripId, 11, 11);
        var candidate = Place(tripId, 2, 2);
        var segment = Segment(tripId);
        segment.FromPlaceId = originalFrom.Id;
        segment.FromPlace = originalFrom;
        segment.ToPlaceId = originalTo.Id;
        segment.ToPlace = originalTo;
        segment.Waypoints.Add(new SegmentWaypoint { SegmentId = segment.Id, PlaceId = candidate.Id, Place = candidate, Position = 0 });
        var before = Snapshot(segment);
        var proposed = proposal(tripId, originalFrom, originalTo, candidate);

        var result = SegmentRouteReconciler.Reconcile(segment, proposed.Item1, proposed.Item2, proposed.Item3, proposed.Item4);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(before, Snapshot(segment));
    }

    /// <summary>Enumerates ownership, ordering, endpoint, location, and geometry rejection cases.</summary>
    public static TheoryData<Func<Guid, Place, Place, Place, (Place?, Place?, SegmentWaypointProposal[], LineString?)>> InvalidProposals => new()
    {
        (tripId, from, to, via) => (from, to, [new(Place(Guid.NewGuid(), 2, 2), 0, null)], null),
        (tripId, from, to, via) => (from, to, [new(via, 0, null), new(via, 1, null)], null),
        (tripId, from, to, via) => (from, to, [new(from, 0, null)], null),
        (tripId, from, to, via) => (from, to, [new(to, 0, null)], null),
        (tripId, from, to, via) => (null, to, [new(via, 0, null)], null),
        (tripId, from, to, via) => (from, null, [new(via, 0, null)], null),
        (tripId, from, to, via) => (from, to, [new(Place(tripId, null, null), 0, null)], null),
        (tripId, from, to, via) => (from, to, [new(via, 1, null)], null),
        (tripId, from, to, via) => (from, to, [new(via, 0, null), new(Place(tripId, 3, 3), 0, null)], null),
        (tripId, from, to, via) => (from, to, [new(via, 0, null)], Line(Coordinate(10, 10), Coordinate(2, 2), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, to, [new(via, 0, 1)], null),
        (tripId, from, to, via) => (from, to, [new(via, 0, 1), new(Place(tripId, 3, 3), 1, 1)], Line(Coordinate(10, 10), Coordinate(2, 2), Coordinate(3, 3), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, to, [new(via, 0, 2), new(Place(tripId, 3, 3), 1, 1)], Line(Coordinate(10, 10), Coordinate(3, 3), Coordinate(2, 2), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, to, [new(via, 0, 0)], Line(Coordinate(10, 10), Coordinate(2, 2), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, to, [new(via, 0, 2)], Line(Coordinate(10, 10), Coordinate(2, 2), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, to, [new(via, 0, 1)], Line(Coordinate(10, 10), Coordinate(2.000001, 2), Coordinate(11, 11))),
        (tripId, from, to, via) => (from, from, [new(via, 0, 1)], Line(Coordinate(10, 10), Coordinate(2, 2), Coordinate(10.000001, 10)))
    };

    /// <summary>The inclusive tolerance boundary is accepted independently on both axes.</summary>
    [Fact]
    public void Reconcile_CoordinateAtToleranceBoundary_IsAccepted()
    {
        var tripId = Guid.NewGuid();
        var from = Place(tripId, 1, 1);
        var via = Place(tripId, 2, 2);
        var to = Place(tripId, 3, 3);
        var geometry = Line(Coordinate(1.0000001, 0.9999999), Coordinate(2.0000001, 1.9999999), Coordinate(3.0000001, 2.9999999));

        var result = SegmentRouteReconciler.Reconcile(Segment(tripId), from, to, [new(via, 0, 1)], geometry);

        Assert.True(result.Succeeded);
    }

    private static Segment Segment(Guid? tripId = null) => new() { Id = Guid.NewGuid(), TripId = tripId ?? Guid.NewGuid(), UserId = "owner" };

    private static Place Place(Guid tripId, double? longitude, double? latitude) => new()
    {
        Id = Guid.NewGuid(),
        Region = new Region { Id = Guid.NewGuid(), TripId = tripId, UserId = "owner" },
        RegionId = Guid.NewGuid(),
        UserId = "owner",
        Location = longitude.HasValue ? new Point(longitude.Value, latitude!.Value) { SRID = 4326 } : null
    };

    private static Coordinate Coordinate(double x, double y) => new(x, y);

    private static LineString Line(params Coordinate[] coordinates) => new(coordinates) { SRID = 4326 };

    private static string Snapshot(Segment segment) =>
        $"{segment.FromPlaceId}|{segment.ToPlaceId}|{segment.RouteGeometry?.AsText()}|{string.Join(';', segment.Waypoints.Select(waypoint => $"{waypoint.PlaceId}:{waypoint.Position}:{waypoint.RouteVertexIndex}"))}";

    private sealed class CoordinateComparer : IEqualityComparer<Coordinate>
    {
        internal static readonly CoordinateComparer Instance = new();
        public bool Equals(Coordinate? left, Coordinate? right) => left?.Equals2D(right) == true;
        public int GetHashCode(Coordinate coordinate) => HashCode.Combine(coordinate.X, coordinate.Y);
    }
}
