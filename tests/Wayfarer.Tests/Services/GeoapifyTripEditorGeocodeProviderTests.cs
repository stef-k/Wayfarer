using System.Net;
using System.Text;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the bounded Geoapify Trip Editor autocomplete adapter contract.</summary>
public sealed class GeoapifyTripEditorGeocodeProviderTests
{
    [Fact]
    public async Task SearchAsyncContactsExactEndpointOnceAndNormalizesResult()
    {
        var handler = new RecordingHandler("""{"results":[{"place_id":"abc","name":"Acropolis","formatted":"Acropolis, Athens","address_line1":"Acropolis","city":"Athens","category":"tourism","result_type":"amenity","lat":37.97,"lon":23.72}]}""");
        var provider = new GeoapifyTripEditorGeocodeProvider(new HttpClient(handler));

        var outcome = await provider.SearchAsync("athens acropolis", 6, "secret-key", CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Success, outcome.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://api.geoapify.com/v1/geocode/autocomplete", handler.Request!.GetLeftPart(UriPartial.Path));
        Assert.Contains("text=athens%20acropolis", handler.Request.Query);
        Assert.Contains("format=json", handler.Request.Query);
        Assert.Contains("lang=en", handler.Request.Query);
        Assert.Contains("limit=6", handler.Request.Query);
        Assert.Contains("apiKey=secret-key", handler.Request.Query);
        var result = Assert.Single(outcome.Response!.Results);
        Assert.Equal("geoapify:abc", result.Id);
        Assert.Equal("geoapify", result.Provider);
        Assert.Contains("Geoapify", outcome.Response.Attribution);
        Assert.Contains("OpenStreetMap", outcome.Response.Attribution);
    }

    [Theory]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":[{\"formatted\":\"Bad\",\"lat\":91,\"lon\":0}]}")]
    [InlineData("{\"results\":[{\"formatted\":9,\"lat\":1,\"lon\":2}]}")]
    public async Task SearchAsyncRejectsMalformedOrInvalidResults(string payload)
    {
        var provider = new GeoapifyTripEditorGeocodeProvider(new HttpClient(new RecordingHandler(payload)));

        var outcome = await provider.SearchAsync("athens", 6, "secret-key", CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Malformed, outcome.Status);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task SearchAsyncRejectsOversizedResponse()
    {
        var provider = new GeoapifyTripEditorGeocodeProvider(new HttpClient(new RecordingHandler(new string('x', 256 * 1024 + 1))));

        var outcome = await provider.SearchAsync("athens", 6, "secret-key", CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Malformed, outcome.Status);
    }

    private sealed class RecordingHandler(string payload) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
