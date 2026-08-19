using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies protected-anchor restoration, budgeting, ordering, and closed loops.</summary>
public sealed class ProviderRouteGeometryValidatorTests
{
    private readonly ProviderRouteGeometryValidator _validator = new();

    [Fact]
    public void Validate_RestoresSnappedAnchorsAndCompleteIndices()
    {
        RouteCoordinate[] anchors = [new(23.7, 37.9), new(23.75, 37.95), new(23.8, 38.0)];
        var route = new OsrmRouteResult(true,
            [new(23.70001, 37.90001), new(23.74, 37.94), new(23.75001, 37.95001), new(23.8, 38.0)],
            [new(23.70001, 37.90001), new(23.75001, 37.95001), new(23.8, 38.0)], null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(anchors[0], result.Geometry![0]);
        Assert.Equal(anchors[1], result.Geometry[result.WaypointIndices![1]]);
        Assert.Equal(anchors[2], result.Geometry[^1]);
        Assert.Equal([0, 2, 3], result.WaypointIndices);
    }

    [Fact]
    public void Validate_InsertsExactAnchorOnlyOnUnambiguousSegment()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.001, 0), new(0.002, 0)];
        var route = new OsrmRouteResult(true, [new(0, 0), new(0.002, 0)], anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(anchors, result.Geometry);
        Assert.Equal([0, 1, 2], result.WaypointIndices);
    }

    [Fact]
    public void Validate_HandlesExactClosedLoopWithDistinctOrderedIndices()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0, 0)];
        var route = new OsrmRouteResult(true, anchors, anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([0, 1, 2], result.WaypointIndices);
        Assert.Equal(result.Geometry![0], result.Geometry[^1]);
    }

    [Fact]
    public void Validate_RejectsReorderedProviderWaypoints()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0.02, 0.02)];
        var route = new OsrmRouteResult(true, anchors, [anchors[0], anchors[2], anchors[1]], null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-waypoints-incompatible", result.ErrorCode);
    }

    [Fact]
    public void Validate_ProtectsEveryAnchorDuringOversizedSimplification()
    {
        var geometry = Enumerable.Range(0, 1201).Select(index => new RouteCoordinate(index / 100000d, Math.Sin(index / 20d) / 100000d)).ToArray();
        RouteCoordinate[] anchors = [geometry[0], geometry[600], geometry[^1]];
        var route = new OsrmRouteResult(true, geometry, anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Geometry!.Count <= 1000);
        Assert.Equal(anchors, result.WaypointIndices!.Select(index => result.Geometry[index]).ToArray());
    }
}
