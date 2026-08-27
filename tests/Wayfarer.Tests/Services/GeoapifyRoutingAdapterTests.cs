using System.Net;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks Geoapify's documented JSON request and leg-relative route response contract.</summary>
public sealed class GeoapifyRoutingAdapterTests
{
    [Fact]
    public void RequestUsesExactClosedModeAndPreservesOrderedWaypoints()
    {
        var request = GeoapifyRoutingAdapter.BuildRelativeRequest("drive",
            [new(20, 10), new(21, 11), new(22, 12)], "secret value");

        Assert.StartsWith("v1/routing?waypoints=10,20%7C11,21%7C12,22&mode=drive&format=json&lang=en", request);
        Assert.Contains("details=instruction_details&type=balanced&traffic=free_flow", request);
        Assert.Contains("intermediate_waypoint_mode=stopover", request);
        Assert.EndsWith("apiKey=secret%20value", request);
    }

    [Fact]
    public async Task MoreThanTwentyFiveGeoapifyAnchorsFailBeforeExecutionOrAdmission()
    {
        var client = new ProviderRouteClient(null!, null!, null!, null!);
        var execution = new ResolvedRoutingProviderExecution(
            new RoutingProviderConfiguration { AdapterType = RoutingAdapterType.Geoapify }, "drive", "secret",
            RoutingProviderSelectionMode.Personal, 1, 1, 1, 1, 1, "Geoapify", null, null, "owner");
        var anchors = Enumerable.Range(0, 26).Select(index => new RouteCoordinate(index, 10)).ToArray();

        var result = await client.RouteAsync(execution, anchors,
            _ => throw new InvalidOperationException("Authority admission must not run."), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("routing-cost-invalid", result.ErrorCode);
    }

    [Fact]
    public async Task DocumentedSingleLegJsonNormalizesCompleteRoute()
    {
        using var response = Response(SingleLegJson);
        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.True(result.Succeeded);
        Assert.Equal([new(20, 10), new(21, 11)], result.Geometry);
        Assert.Equal(1234, result.DistanceMetres);
        Assert.Equal(321, result.DurationSeconds);
        Assert.Equal(new RouteInstruction("Continue", "Straight", 0, 1, 1234, 321), Assert.Single(result.Instructions));
    }

    [Fact]
    public async Task DocumentedMultiLegJsonTranslatesIndicesAndMapsIntermediateAnchorStructurally()
    {
        using var response = Response(MultiLegJson);
        var result = await GeoapifyRoutingAdapter.ParseAsync(response,
            [new(20, 10), new(21, 11), new(22, 12)]);

        Assert.True(result.Succeeded);
        Assert.Equal([new(20, 10), new(20.5, 10.5), new(21, 11), new(21.5, 11.5), new(22, 12)], result.Geometry);
        Assert.Equal(1, result.Geometry.Count(point => point == new RouteCoordinate(21, 11)));
        Assert.Equal([new(20, 10), new(21, 11), new(22, 12)], result.Waypoints);
        Assert.Equal(30, result.DistanceMetres);
        Assert.Equal(10, result.DurationSeconds);
        Assert.Equal([
            new RouteInstruction("First", "Straight", 0, 1, 4, 2),
            new RouteInstruction("Second", "None", 3, 4, 12, 3)
        ], result.Instructions);
    }

    [Theory]
    [InlineData("[21,11],[21.5,11.5]", "[21.1,11],[21.5,11.5]")]
    [InlineData("[[21,11],[21.5,11.5],[22,12]]", "[[21,11]]")]
    public async Task MalformedOrDisconnectedLegGeometryFailsClosed(string current, string mutation)
    {
        using var response = Response(MultiLegJson.Replace(current, mutation, StringComparison.Ordinal));
        var result = await GeoapifyRoutingAdapter.ParseAsync(response,
            [new(20, 10), new(21, 11), new(22, 12)]);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-response-invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("\"from_index\":0,\"to_index\":1", "\"from_index\":1,\"to_index\":1")]
    [InlineData("\"from_index\":0,\"to_index\":1", "\"from_index\":1,\"to_index\":0")]
    [InlineData("\"from_index\":0,\"to_index\":1", "\"from_index\":0,\"to_index\":2")]
    public async Task InvalidOrDiscontinuousLegRelativeStepFailsClosed(string current, string mutation)
    {
        using var response = Response(SingleLegJson.Replace(current, mutation, StringComparison.Ordinal));
        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DiscontinuousLegRelativeStepsFailClosed()
    {
        using var response = Response(MultiLegJson.Replace(
            "\"from_index\":1,\"to_index\":2", "\"from_index\":0,\"to_index\":2", StringComparison.Ordinal));
        var result = await GeoapifyRoutingAdapter.ParseAsync(response,
            [new(20, 10), new(21, 11), new(22, 12)]);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("\"distance\":1234,\"time\":321,\"distance_units\"", "\"distance\":1234.0100001,\"time\":321,\"distance_units\"")]
    [InlineData("\"distance\":1234,\"time\":321,\"steps\"", "\"distance\":1234.0100001,\"time\":321,\"steps\"")]
    public async Task ContradictoryTotalsBeyondSpecifiedToleranceFailClosed(string current, string mutation)
    {
        using var response = Response(SingleLegJson.Replace(current, mutation, StringComparison.Ordinal));
        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task MissingOptionalInstructionDataOmitsOnlyNormalizedInstructions()
    {
        var json = SingleLegJson.Replace("\"instruction\":{\"text\":\"Continue\",\"type\":\"Straight\"},", string.Empty);
        using var response = Response(json);
        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Instructions);
    }

    [Fact]
    public async Task ZeroLengthDestinationStepRemainsNormalized()
    {
        var destination = """
            ,{"instruction":{"text":"Destination","type":"Destination"},
            "from_index":1,"to_index":1,"distance":0,"time":0}
            """;
        using var response = Response(SingleLegJson.Replace("]}]}]}", $"{destination}]}}]}}]}}", StringComparison.Ordinal));

        var result = await GeoapifyRoutingAdapter.ParseAsync(response, [new(20, 10), new(21, 11)]);

        Assert.True(result.Succeeded);
        Assert.Equal(new RouteInstruction("Destination", "Destination", 1, 1, 0, 0), result.Instructions[1]);
    }

    private static HttpResponseMessage Response(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private const string SingleLegJson = """
        {"results":[{"distance":1234,"time":321,"distance_units":"meters","geometry":[[[20,10],[21,11]]],
        "legs":[{"distance":1234,"time":321,"steps":[{"instruction":{"text":"Continue","type":"Straight"},
        "from_index":0,"to_index":1,"distance":1234,"time":321}]}]}]}
        """;

    private const string MultiLegJson = """
        {"results":[{"distance":30,"time":10,"distance_units":"METERS",
        "geometry":[[[20,10],[20.5,10.5],[21,11]],[[21,11],[21.5,11.5],[22,12]]],
        "legs":[{"distance":10,"time":4,"steps":[
        {"instruction":{"text":"First","type":"Straight"},"from_index":0,"to_index":1,"distance":4,"time":2},
        {"instruction":null,"from_index":1,"to_index":2,"distance":6,"time":2}]},
        {"distance":20,"time":6,"steps":[
        {"instruction":{"text":"   ","type":"Turn"},"from_index":0,"to_index":1,"distance":8,"time":3},
        {"instruction":{"text":"Second"},"from_index":1,"to_index":2,"distance":12,"time":3}]}]}]}
        """;
}
