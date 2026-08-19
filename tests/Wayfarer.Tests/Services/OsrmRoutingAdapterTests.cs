using System.Net;
using System.Text;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies the explicit OSRM request and untrusted-response contract.</summary>
public sealed class OsrmRoutingAdapterTests
{
    [Fact]
    public void BuildRequest_UsesExactRouteContractAndInvariantCoordinates()
    {
        var request = OsrmRoutingAdapter.BuildRelativeRequest("driving", [
            new RouteCoordinate(23.7275, 37.9838), new RouteCoordinate(2.3522, 48.8566)]);

        Assert.Equal(
            "route/v1/driving/23.7275,37.9838;2.3522,48.8566?alternatives=false&steps=false&overview=full&geometries=geojson",
            request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../driving")]
    [InlineData("driving?key=secret")]
    public void BuildRequest_RejectsUnsafeProfile(string profile) => Assert.Throws<ArgumentException>(() =>
        OsrmRoutingAdapter.BuildRelativeRequest(profile, [new(1, 2), new(3, 4)]));

    [Fact]
    public async Task ParseResponse_RequiresOneValidNonEmptyRouteAndIgnoresProviderMeasurements()
    {
        const string json = """
            {"code":"Ok","routes":[{"distance":999999,"duration":888888,"geometry":{"type":"LineString","coordinates":[[23.7,37.9],[23.8,38.0]]}}],"waypoints":[{"location":[23.7,37.9]},{"location":[23.8,38.0]}]}
            """;

        var result = await OsrmRoutingAdapter.ParseAsync(Response(json), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([new RouteCoordinate(23.7, 37.9), new RouteCoordinate(23.8, 38.0)], result.Geometry);
        Assert.Equal(2, result.Waypoints.Count);
    }

    [Theory]
    [InlineData("{\"code\":\"NoRoute\",\"routes\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4]]}},{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4]]}}]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[181,2],[3,4]]}}]}")]
    public async Task ParseResponse_RejectsAmbiguousOrInvalidResponses(string json)
    {
        var result = await OsrmRoutingAdapter.ParseAsync(Response(json), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-response-invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("{\"code\":1,\"routes\":[],\"waypoints\":[]}")]
    [InlineData("{\"code\":null,\"routes\":[],\"waypoints\":[]}")]
    [InlineData("{\"code\":{},\"routes\":[],\"waypoints\":[]}")]
    [InlineData("{\"code\":[],\"routes\":[],\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":{},\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[1],\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":1}],\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":1,\"coordinates\":[]}}],\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":{}}}],\"waypoints\":[]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[\"1\",2],[3,4]]}}],\"waypoints\":[{\"location\":[1,2]},{\"location\":[3,4]}]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4]]}}],\"waypoints\":{}}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4]]}}],\"waypoints\":[1,{\"location\":[3,4]}]}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4]]}}],\"waypoints\":[{\"location\":{}},{\"location\":[3,4]}]}")]
    public async Task ParseResponse_BoundsEveryProviderControlledTypeFailure(string json)
    {
        var result = await OsrmRoutingAdapter.ParseAsync(Response(json), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-response-invalid", result.ErrorCode);
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
