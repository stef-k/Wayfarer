using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests the Trip Editor geocode provider and local cache/rate-limit service.
/// </summary>
public sealed class TripEditorGeocodeSearchServiceTests
{
    [Fact]
    public async Task SearchAsyncReturnsCachedResultWithoutSecondProviderCall()
    {
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var service = BuildService(provider, clock);

        var first = await service.SearchAsync("  Athens   Acropolis ", 6, CancellationToken.None);
        var second = await service.SearchAsync("athens acropolis", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, first.Status);
        Assert.Equal(TripEditorGeocodeSearchStatus.Success, second.Status);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SearchAsyncReturnsLocalRateLimitWithoutSleeping()
    {
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var service = BuildService(provider, clock);

        var first = await service.SearchAsync("athens one", 6, CancellationToken.None);
        var second = await service.SearchAsync("athens two", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, first.Status);
        Assert.Equal(TripEditorGeocodeSearchStatus.LocalRateLimited, second.Status);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SearchAsyncAllowsNextProviderCallAfterClockAdvances()
    {
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var service = BuildService(provider, clock);

        await service.SearchAsync("athens one", 6, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await service.SearchAsync("athens two", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, second.Status);
        Assert.Equal(2, provider.CallCount);
    }

    [Theory]
    [InlineData(TripEditorGeocodeProviderStatus.RateLimited, TripEditorGeocodeSearchStatus.ProviderRateLimited)]
    [InlineData(TripEditorGeocodeProviderStatus.Timeout, TripEditorGeocodeSearchStatus.ProviderTimeout)]
    [InlineData(TripEditorGeocodeProviderStatus.Unavailable, TripEditorGeocodeSearchStatus.ProviderUnavailable)]
    [InlineData(TripEditorGeocodeProviderStatus.Malformed, TripEditorGeocodeSearchStatus.ProviderMalformed)]
    public async Task SearchAsyncMapsProviderFailures(TripEditorGeocodeProviderStatus providerStatus, TripEditorGeocodeSearchStatus expectedStatus)
    {
        var provider = new FakeProvider { Result = TripEditorGeocodeProviderResult.Failure(providerStatus) };
        var service = BuildService(provider, new FakeClock());

        var result = await service.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public async Task NominatimProviderSendsUserAgentAndReferer()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        var options = Options.Create(new TripEditorGeocodeOptions
        {
            NominatimSearchEndpoint = "https://nominatim.openstreetmap.org/search",
            NominatimUserAgent = "WayfarerTests/1.0",
            Referer = "https://wayfarer.example.test"
        });
        var provider = new NominatimTripEditorGeocodeProvider(new HttpClient(handler), options);

        await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Contains("WayfarerTests/1.0", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.Equal("https://wayfarer.example.test/", handler.LastRequest.Headers.Referrer?.ToString());
    }

    [Fact]
    public async Task RegisteredNominatimProviderSendsConfiguredUserAgent()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        var provider = BuildRegisteredNominatimProvider(handler, new Dictionary<string, string?>
        {
            ["TripEditorGeocode:NominatimSearchEndpoint"] = "https://nominatim.openstreetmap.org/search",
            ["TripEditorGeocode:NominatimUserAgent"] = "WayfarerConfigured/1.0",
            ["Application:ContactEmail"] = "ignored@example.test"
        });

        await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal("WayfarerConfigured/1.0", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.DoesNotContain("ignored@example.test", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisteredNominatimProviderUsesPlainUserAgentWhenConfiguredUserAgentMissingOrBlank(string? configuredUserAgent)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        var provider = BuildRegisteredNominatimProvider(handler, new Dictionary<string, string?>
        {
            ["TripEditorGeocode:NominatimSearchEndpoint"] = "https://nominatim.openstreetmap.org/search",
            ["TripEditorGeocode:NominatimUserAgent"] = configuredUserAgent,
            ["Application:ContactEmail"] = "ignored@example.test"
        });

        await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal("Wayfarer/1.0", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.DoesNotContain("ignored@example.test", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task RegisteredNominatimProviderUsesPlainUserAgentWhenGeocodeOptionsAreUnbound()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        var provider = BuildRegisteredNominatimProvider(handler, new Dictionary<string, string?>
        {
            ["Application:ContactEmail"] = "ignored@example.test"
        });

        await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal("Wayfarer/1.0", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.DoesNotContain("ignored@example.test", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task NominatimProviderMapsNoResultsToSuccess()
    {
        var provider = BuildNominatimProvider("[]");

        var result = await provider.SearchAsync("missing", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Success, result.Status);
        Assert.Empty(result.Response!.Results);
        Assert.NotEmpty(result.Response.Attribution);
    }

    [Fact]
    public async Task NominatimProviderParsesSuccessfulResult()
    {
        var provider = BuildNominatimProvider("""
        [{
          "place_id": 123,
          "name": "Acropolis",
          "display_name": "Acropolis, Athens, Greece",
          "lat": "37.9715",
          "lon": "23.7257",
          "category": "tourism",
          "type": "attraction",
          "address": { "city": "Athens", "country": "Greece" }
        }]
        """);

        var result = await provider.SearchAsync("acropolis", 6, CancellationToken.None);

        var item = Assert.Single(result.Response!.Results);
        Assert.Equal("nominatim", item.Provider);
        Assert.Equal("Acropolis", item.Name);
        Assert.Equal("Athens, Greece", item.Address);
        Assert.Equal(37.9715, item.Latitude, 4);
        Assert.Equal(23.7257, item.Longitude, 4);
    }

    [Fact]
    public async Task NominatimProviderMapsMalformedPayload()
    {
        var provider = BuildNominatimProvider("""{ "not": "an array" }""");

        var result = await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task NominatimProviderRejectsNonObjectResult()
    {
        var provider = BuildNominatimProvider("[9]");

        var result = await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task NominatimProviderRejectsOversizedBodyWithoutContentLength()
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 256 * 1024 + 1))));
        content.Headers.ContentLength = null;
        var provider = new NominatimTripEditorGeocodeProvider(
            new HttpClient(new ContentHandler(content)), Options.Create(new TripEditorGeocodeOptions()));

        var result = await provider.SearchAsync("athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeProviderStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task NominatimProviderPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CancelingHandler(cancellation);
        var provider = new NominatimTripEditorGeocodeProvider(new HttpClient(handler), Options.Create(new TripEditorGeocodeOptions()));

        await Assert.ThrowsAsync<TaskCanceledException>(() => provider.SearchAsync("athens", 6, cancellation.Token));
    }

    private static TripEditorGeocodeSearchService BuildService(FakeProvider provider, FakeClock clock) =>
        new(
            provider,
            new MemoryCache(new MemoryCacheOptions()),
            new TripEditorGeocodeRateLimiter(clock),
            Options.Create(new TripEditorGeocodeOptions { CacheSeconds = 60, MinimumIntervalMilliseconds = 1000 }));

    private static NominatimTripEditorGeocodeProvider BuildNominatimProvider(string response) =>
        new(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, response)),
            Options.Create(new TripEditorGeocodeOptions { NominatimSearchEndpoint = "https://nominatim.openstreetmap.org/search" }));

    private static ITripEditorGeocodeProvider BuildRegisteredNominatimProvider(
        CapturingHandler handler,
        Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddTripEditorGeocodeSearch(configuration);
        services.AddHttpClient<ITripEditorGeocodeProvider, NominatimTripEditorGeocodeProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<ITripEditorGeocodeProvider>();
    }

    private sealed class FakeProvider : ITripEditorGeocodeProvider
    {
        public int CallCount { get; private set; }

        public TripEditorGeocodeProviderResult Result { get; init; } =
            TripEditorGeocodeProviderResult.Success(new EditorGeocodeSearchResponseDto("athens", "Data source", new[]
            {
                new EditorGeocodeSearchResultDto("nominatim:1", "nominatim", "Athens", "Athens, Greece", "Greece", "place", "city", 37.9838, 23.7275)
            }));

        public Task<TripEditorGeocodeProviderResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            CallCount += 1;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeClock : ITripEditorGeocodeClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan value)
        {
            UtcNow = UtcNow.Add(value);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _response;

        public CapturingHandler(HttpStatusCode statusCode, string response)
        {
            _statusCode = statusCode;
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelingHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            throw new TaskCanceledException("Caller canceled request.");
        }
    }

    private sealed class ContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
