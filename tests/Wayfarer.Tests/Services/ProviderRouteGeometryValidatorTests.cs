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
        var route = new ProviderRouteResult(true,
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
        var route = new ProviderRouteResult(true, [new(0, 0), new(0.002, 0)], anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(anchors, result.Geometry);
        Assert.Equal([0, 1, 2], result.WaypointIndices);
    }

    [Fact]
    public void Validate_HandlesExactClosedLoopWithDistinctOrderedIndices()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0, 0)];
        var route = new ProviderRouteResult(true, anchors, anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([0, 1, 2], result.WaypointIndices);
        Assert.Equal(result.Geometry![0], result.Geometry[^1]);
    }

    [Fact]
    public void Validate_RejectsReorderedProviderWaypoints()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0.02, 0.02)];
        var route = new ProviderRouteResult(true, anchors, [anchors[0], anchors[2], anchors[1]], null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-waypoints-incompatible", result.ErrorCode);
    }

    [Fact]
    public void Validate_ProtectsEveryAnchorDuringOversizedSimplification()
    {
        var geometry = Enumerable.Range(0, 1201).Select(index => new RouteCoordinate(index / 100000d, Math.Sin(index / 20d) / 100000d)).ToArray();
        RouteCoordinate[] anchors = [geometry[0], geometry[600], geometry[^1]];
        var route = new ProviderRouteResult(true, geometry, anchors, null);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Geometry!.Count <= 1000);
        Assert.Equal(anchors, result.WaypointIndices!.Select(index => result.Geometry[index]).ToArray());
    }

    [Fact]
    public async Task Validate_UsesGeoapifyLegBoundaryWhenIntermediateAnchorIsRevisited()
    {
        const string json = """
            {"results":[{"distance":30,"time":10,"geometry":[[{"lon":20,"lat":10},{"lon":20.5,"lat":10.5},{"lon":21,"lat":11}],[{"lon":21,"lat":11},{"lon":21,"lat":11},{"lon":22,"lat":12}]],
            "legs":[{"distance":10,"time":4,"steps":[{"from_index":0,"to_index":2,"distance":10,"time":4}]},
            {"distance":20,"time":6,"steps":[{"from_index":0,"to_index":2,"distance":20,"time":6}]}]}]}
            """;
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        RouteCoordinate[] anchors = [new(20, 10), new(21, 11), new(22, 12)];
        var route = await GeoapifyRoutingAdapter.ParseAsync(response, anchors);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(anchors, result.WaypointIndices!.Select(index => result.Geometry![index]).ToArray());
    }

    /// <summary>Proves structural identity selects the provider leg boundary rather than an earlier equal coordinate.</summary>
    [Fact]
    public void Validate_StructuralWaypointIdentity_IgnoresEarlierEqualCoordinate()
    {
        RouteCoordinate[] anchors = [new(20, 10), new(21, 11), new(22, 12)];
        RouteCoordinate[] geometry = [anchors[0], anchors[1], new(21.5, 11.5), anchors[1], anchors[2]];
        var route = WithStructuralIndices(new ProviderRouteResult(true, geometry, anchors, null), [0, 3, 4]);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([0, 3, 4], result.WaypointIndices);
        Assert.Equal(anchors[1], result.Geometry![1]);
        Assert.DoesNotContain(1, result.WaypointIndices!);
        Assert.Equal(anchors, result.WaypointIndices!.Select(index => result.Geometry![index]).ToArray());
    }

    [Theory]
    [MemberData(nameof(MalformedStructuralIndices))]
    public void Validate_RejectsMalformedStructuralIndices(IReadOnlyList<int> indices)
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0.02, 0.02)];
        var route = WithStructuralIndices(new ProviderRouteResult(true, anchors, anchors, null), indices);

        var result = _validator.Validate(anchors, route, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-route-invalid", result.ErrorCode);
    }

    [Fact]
    public void Validate_ResultWithoutStructuralIndicesRetainsAmbiguityRejection()
    {
        RouteCoordinate[] anchors = [new(0, 0), new(0.01, 0.01), new(0.02, 0.02)];
        RouteCoordinate[] geometry = [anchors[0], anchors[1], anchors[1], anchors[2]];

        var result = _validator.Validate(anchors, new ProviderRouteResult(true, geometry, anchors, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-anchor-ambiguous", result.ErrorCode);
    }

    public static TheoryData<IReadOnlyList<int>> MalformedStructuralIndices => new()
    {
        new[] { 0, 2 },
        new[] { 0, 2, 1 },
        new[] { 0, 1, 3 }
    };

    private static ProviderRouteResult WithStructuralIndices(ProviderRouteResult route, IReadOnlyList<int> indices)
        => route with { StructuralWaypointIndices = indices };
}
