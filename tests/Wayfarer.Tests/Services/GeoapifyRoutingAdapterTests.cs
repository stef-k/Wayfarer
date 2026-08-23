using System.Net;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks Geoapify request and complete normalized route parsing.</summary>
public sealed class GeoapifyRoutingAdapterTests
{
    [Fact]
    public void RequestUsesExactClosedModeAndPreservesOrderedWaypoints()
    {
        var request = GeoapifyRoutingAdapter.BuildRelativeRequest("drive",
            [new(20, 10), new(21, 11), new(22, 12)], "secret");

        Assert.StartsWith("v1/routing?waypoints=10,20%7C11,21%7C12,22&mode=drive", request, StringComparison.Ordinal);
        Assert.Contains("details=instruction_details&type=balanced&traffic=free_flow", request, StringComparison.Ordinal);
        Assert.Contains("intermediate_waypoint_mode=stopover", request, StringComparison.Ordinal);
        Assert.EndsWith("apiKey=secret", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteRouteNormalizesGeometryMetricsAndInstructions()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidJson) };

        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.True(result.Succeeded);
        Assert.Equal(1234, result.DistanceMetres);
        Assert.Equal(321, result.DurationSeconds);
        Assert.Equal(2, result.Geometry.Count);
        Assert.Single(result.Instructions);
        Assert.Equal("Continue", result.Instructions[0].Text);
    }

    [Fact]
    public async Task PartialOrWrongAnchorRouteFailsClosed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ValidJson.Replace("[21,11]", "[30,30]", StringComparison.Ordinal)) };

        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Geometry);
    }

    private const string ValidJson = """
        {"results":[{"distance":1234,"time":321,"geometry":{"type":"LineString","coordinates":[[20,10],[21,11]]},
        "legs":[{"distance":1234,"time":321,"steps":[{"instruction":{"text":"Continue","type":"Straight"},
        "from_index":0,"to_index":1,"distance":1234,"time":321}]}]}]}
        """;
}
