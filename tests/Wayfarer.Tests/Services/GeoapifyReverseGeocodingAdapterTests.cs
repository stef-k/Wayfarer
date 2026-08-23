using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves Geoapify reverse parsing, request shape, and failure containment with fake HTTP.</summary>
public sealed class GeoapifyReverseGeocodingAdapterTests
{
    [Fact]
    public async Task ValidFeatureMapsExactPersistentFields()
    {
        const string json = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{
            "formatted":"12 Main Street, Town","address_line1":"12 Main Street","housenumber":"12",
            "street":"Main Street","postcode":"12345","city":"Town","state":"Region","country":"Country"}}]}
            """;
        var handler = new FakeHandler(json);
        var adapter = new GeoapifyReverseGeocodingAdapter(new HttpClient(handler));

        var result = await adapter.ReverseAsync(10.5, 20.25, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal("12 Main Street, Town", result.Value!.FullAddress);
        Assert.Equal("12 Main Street", result.Value.Address);
        Assert.Equal("12", result.Value.AddressNumber);
        Assert.Equal("Main Street", result.Value.StreetName);
        Assert.Equal("12345", result.Value.PostCode);
        Assert.Equal("Town", result.Value.Place);
        Assert.Equal("Region", result.Value.Region);
        Assert.Equal("Country", result.Value.Country);
        Assert.Equal("api.geoapify.com", handler.Uri!.Host);
        Assert.Equal("/v1/geocode/reverse", handler.Uri.AbsolutePath);
        Assert.Contains("format=geojson&lang=en&limit=1", handler.Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[]}")]
    [InlineData("{\"type\":\"FeatureCollection\"}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"properties\":{}}]}")]
    public async Task EmptyOrMalformedResponseNeverProducesPersistenceAuthority(string json)
    {
        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "secret");

        Assert.False(result.Succeeded);
        Assert.Null(result.Authority);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
