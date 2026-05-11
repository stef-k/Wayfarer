using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>
/// Search-only geocode provider abstraction used by the Trip Editor proxy.
/// </summary>
public interface ITripEditorGeocodeProvider
{
    /// <summary>Searches the external provider for a normalized address query.</summary>
    Task<TripEditorGeocodeProviderResult> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Application-level Trip Editor geocode search service.
/// </summary>
public interface ITripEditorGeocodeSearchService
{
    /// <summary>Searches through the configured provider with local cache and rate limiting.</summary>
    Task<TripEditorGeocodeSearchOutcome> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Provides deterministic time for testable geocode rate limiting.
/// </summary>
public interface ITripEditorGeocodeClock
{
    /// <summary>Gets the current UTC timestamp.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// System clock for Trip Editor geocode rate limiting.
/// </summary>
public sealed class SystemTripEditorGeocodeClock : ITripEditorGeocodeClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Serializes uncached provider calls to at most one request per configured interval.
/// </summary>
public sealed class TripEditorGeocodeRateLimiter
{
    private readonly ITripEditorGeocodeClock _clock;
    private readonly object _gate = new();
    private DateTimeOffset _nextAvailableAt = DateTimeOffset.MinValue;

    /// <summary>Initializes the app-wide geocode rate limiter.</summary>
    public TripEditorGeocodeRateLimiter(ITripEditorGeocodeClock clock)
    {
        _clock = clock;
    }

    /// <summary>Attempts to reserve one provider request without sleeping.</summary>
    public bool TryAcquire(TimeSpan minimumInterval)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (now < _nextAvailableAt)
            {
                return false;
            }

            _nextAvailableAt = now.Add(minimumInterval);
            return true;
        }
    }
}

/// <summary>
/// Concrete geocode search service that keeps browser traffic on the Wayfarer proxy.
/// </summary>
public sealed class TripEditorGeocodeSearchService : ITripEditorGeocodeSearchService
{
    private readonly ITripEditorGeocodeProvider _provider;
    private readonly IMemoryCache _cache;
    private readonly TripEditorGeocodeRateLimiter _rateLimiter;
    private readonly TripEditorGeocodeOptions _options;

    /// <summary>Initializes the cached/rate-limited geocode search service.</summary>
    public TripEditorGeocodeSearchService(
        ITripEditorGeocodeProvider provider,
        IMemoryCache cache,
        TripEditorGeocodeRateLimiter rateLimiter,
        IOptions<TripEditorGeocodeOptions> options)
    {
        _provider = provider;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<TripEditorGeocodeSearchOutcome> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var cacheKey = $"trip-editor-geocode:{normalized}:{limit}";
        if (_cache.TryGetValue(cacheKey, out EditorGeocodeSearchResponseDto? cached) && cached != null)
        {
            return TripEditorGeocodeSearchOutcome.Success(cached);
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, _options.MinimumIntervalMilliseconds));
        if (!_rateLimiter.TryAcquire(interval))
        {
            return TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.LocalRateLimited);
        }

        var providerResult = await _provider.SearchAsync(normalized, limit, cancellationToken);
        if (providerResult.Status != TripEditorGeocodeProviderStatus.Success || providerResult.Response == null)
        {
            return TripEditorGeocodeSearchOutcome.Failure(providerResult.Status switch
            {
                TripEditorGeocodeProviderStatus.RateLimited => TripEditorGeocodeSearchStatus.ProviderRateLimited,
                TripEditorGeocodeProviderStatus.Timeout => TripEditorGeocodeSearchStatus.ProviderTimeout,
                TripEditorGeocodeProviderStatus.Unavailable => TripEditorGeocodeSearchStatus.ProviderUnavailable,
                TripEditorGeocodeProviderStatus.Malformed => TripEditorGeocodeSearchStatus.ProviderMalformed,
                _ => TripEditorGeocodeSearchStatus.ProviderUnavailable
            });
        }

        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.CacheSeconds));
        _cache.Set(cacheKey, providerResult.Response, ttl);
        return TripEditorGeocodeSearchOutcome.Success(providerResult.Response);
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Public Nominatim-backed geocode search provider.
/// </summary>
public sealed class NominatimTripEditorGeocodeProvider : ITripEditorGeocodeProvider
{
    private const string DefaultUserAgent = "Wayfarer/1.0";
    private const string ProviderName = "nominatim";
    private const string Attribution = "Data © OpenStreetMap contributors, ODbL 1.0.";
    private readonly HttpClient _httpClient;
    private readonly TripEditorGeocodeOptions _options;

    /// <summary>Initializes a Nominatim search adapter.</summary>
    public NominatimTripEditorGeocodeProvider(HttpClient httpClient, IOptions<TripEditorGeocodeOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            var userAgent = string.IsNullOrWhiteSpace(_options.NominatimUserAgent)
                ? DefaultUserAgent
                : _options.NominatimUserAgent;
            if (!_httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(DefaultUserAgent);
            }
        }

        if (_httpClient.DefaultRequestHeaders.Referrer == null
            && !string.IsNullOrWhiteSpace(_options.Referer)
            && Uri.TryCreate(_options.Referer, UriKind.Absolute, out var referer))
        {
            _httpClient.DefaultRequestHeaders.Referrer = referer;
        }
    }

    /// <inheritdoc />
    public async Task<TripEditorGeocodeProviderResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildSearchUri(query, limit));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.RateLimited);
            }

            if (!response.IsSuccessStatusCode)
            {
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Unavailable);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return TripEditorGeocodeProviderResult.Success(new EditorGeocodeSearchResponseDto(
                query,
                Attribution,
                await ParseResultsAsync(stream, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Timeout);
        }
        catch (HttpRequestException)
        {
            return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Unavailable);
        }
        catch (JsonException)
        {
            return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed);
        }
        catch (FormatException)
        {
            return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed);
        }
    }

    private Uri BuildSearchUri(string query, int limit)
    {
        var endpoint = string.IsNullOrWhiteSpace(_options.NominatimSearchEndpoint)
            ? "https://nominatim.openstreetmap.org/search"
            : _options.NominatimSearchEndpoint;
        var builder = new UriBuilder(endpoint);
        var queryParts = new Dictionary<string, string?>
        {
            ["format"] = "jsonv2",
            ["q"] = query,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["addressdetails"] = "1"
        };
        builder.Query = string.Join('&', queryParts.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return builder.Uri;
    }

    private static async Task<IReadOnlyList<EditorGeocodeSearchResultDto>> ParseResultsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Nominatim search response must be an array.");
        }

        var results = new List<EditorGeocodeSearchResultDto>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var displayName = RequiredString(element, "display_name");
            var latitude = double.Parse(RequiredString(element, "lat"), CultureInfo.InvariantCulture);
            var longitude = double.Parse(RequiredString(element, "lon"), CultureInfo.InvariantCulture);
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            {
                throw new FormatException("Nominatim coordinates must be finite.");
            }

            var name = OptionalString(element, "name") ?? displayName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? displayName;
            var id = OptionalString(element, "place_id") ??
                string.Join(':', new[] { OptionalString(element, "osm_type"), OptionalString(element, "osm_id") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            results.Add(new EditorGeocodeSearchResultDto(
                string.IsNullOrWhiteSpace(id) ? $"{ProviderName}:{latitude.ToString(CultureInfo.InvariantCulture)}:{longitude.ToString(CultureInfo.InvariantCulture)}" : $"{ProviderName}:{id}",
                ProviderName,
                name,
                displayName,
                BuildAddress(element) ?? displayName,
                OptionalString(element, "category"),
                OptionalString(element, "type"),
                latitude,
                longitude));
        }

        return results;
    }

    private static string? BuildAddress(JsonElement element)
    {
        if (!element.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = new[] { "road", "neighbourhood", "suburb", "city", "town", "village", "state", "country" }
            .Select(key => OptionalString(address, key))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var text = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ?? throw new JsonException($"Nominatim result missing {propertyName}.");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
}

/// <summary>Status returned by an external geocode provider.</summary>
public enum TripEditorGeocodeProviderStatus
{
    Success,
    RateLimited,
    Timeout,
    Unavailable,
    Malformed
}

/// <summary>Status returned by the Trip Editor geocode service.</summary>
public enum TripEditorGeocodeSearchStatus
{
    Success,
    LocalRateLimited,
    ProviderRateLimited,
    ProviderTimeout,
    ProviderUnavailable,
    ProviderMalformed
}

/// <summary>Provider result wrapper for deterministic proxy status mapping.</summary>
public sealed record TripEditorGeocodeProviderResult(TripEditorGeocodeProviderStatus Status, EditorGeocodeSearchResponseDto? Response)
{
    /// <summary>Builds a successful provider result.</summary>
    public static TripEditorGeocodeProviderResult Success(EditorGeocodeSearchResponseDto response) => new(TripEditorGeocodeProviderStatus.Success, response);

    /// <summary>Builds a failed provider result.</summary>
    public static TripEditorGeocodeProviderResult Failure(TripEditorGeocodeProviderStatus status) => new(status, null);
}

/// <summary>Service result wrapper for deterministic controller status mapping.</summary>
public sealed record TripEditorGeocodeSearchOutcome(TripEditorGeocodeSearchStatus Status, EditorGeocodeSearchResponseDto? Response)
{
    /// <summary>Builds a successful search result.</summary>
    public static TripEditorGeocodeSearchOutcome Success(EditorGeocodeSearchResponseDto response) => new(TripEditorGeocodeSearchStatus.Success, response);

    /// <summary>Builds a failed search result.</summary>
    public static TripEditorGeocodeSearchOutcome Failure(TripEditorGeocodeSearchStatus status) => new(status, null);
}
