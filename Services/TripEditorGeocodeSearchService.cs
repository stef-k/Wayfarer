using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Options;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

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
    Task<TripEditorGeocodeSearchOutcome> SearchAsync(string userId, string query, int limit, CancellationToken cancellationToken);
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
    private readonly ITripEditorGeocodeProvider _nominatim;
    private readonly GeoapifyTripEditorGeocodeProvider? _geoapify;
    private readonly IPersonalProviderStatusReader? _statusReader;
    private readonly IPersonalProviderContactGate? _contactGate;
    private readonly IMemoryCache _cache;
    private readonly TripEditorGeocodeRateLimiter _rateLimiter;
    private readonly TripEditorGeocodeOptions _options;

    /// <summary>Initializes the cached/rate-limited geocode search service.</summary>
    public TripEditorGeocodeSearchService(
        NominatimTripEditorGeocodeProvider nominatim,
        GeoapifyTripEditorGeocodeProvider geoapify,
        IPersonalProviderStatusReader statusReader,
        IPersonalProviderContactGate contactGate,
        IMemoryCache cache,
        TripEditorGeocodeRateLimiter rateLimiter,
        IOptions<TripEditorGeocodeOptions> options)
    {
        _nominatim = nominatim;
        _geoapify = geoapify;
        _statusReader = statusReader;
        _contactGate = contactGate;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _options = options.Value;
    }

    /// <summary>Initializes the Nominatim-only seam retained for focused fallback tests.</summary>
    public TripEditorGeocodeSearchService(
        ITripEditorGeocodeProvider provider, IMemoryCache cache,
        TripEditorGeocodeRateLimiter rateLimiter, IOptions<TripEditorGeocodeOptions> options)
    {
        _nominatim = provider;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _options = options.Value;
    }

    /// <summary>Searches the Nominatim fallback directly for existing provider-bound regression tests.</summary>
    public Task<TripEditorGeocodeSearchOutcome> SearchAsync(
        string query, int limit, CancellationToken cancellationToken) =>
        SearchNominatimAsync(NormalizeQuery(query), limit, cancellationToken);

    /// <inheritdoc />
    public async Task<TripEditorGeocodeSearchOutcome> SearchAsync(string userId, string query, int limit, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var inspection = await _statusReader!.InspectPersistentGeocodingAsync(userId, cancellationToken);
        if (inspection.ProviderKey == null || inspection.ProviderKey == "mapbox")
        {
            return await SearchNominatimAsync(normalized, limit, cancellationToken);
        }

        if (inspection.ProviderKey != "geoapify" || inspection.Category != PersonalProviderAdmissionCategory.Admitted || inspection.Binding == null)
        {
            return TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.ProviderUnavailable);
        }

        if (inspection.Exhausted)
        {
            return await SearchNominatimAsync(normalized, limit, cancellationToken);
        }

        var binding = inspection.Binding;
        var cacheKey = BuildGeoapifyCacheKey(userId, normalized, limit, binding);
        if (_cache.TryGetValue(cacheKey, out EditorGeocodeSearchResponseDto? cached) && cached != null)
        {
            var current = await _statusReader.InspectPersistentGeocodingAsync(userId, cancellationToken);
            return current.Category == PersonalProviderAdmissionCategory.Admitted && current.Binding == binding
                ? TripEditorGeocodeSearchOutcome.Success(cached)
                : TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.ProviderUnavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var admission = await _contactGate!.AdmitAsync(userId, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.Geocoding, 1, cancellationToken);
        if (admission.Category == PersonalProviderAdmissionCategory.Exhausted)
        {
            return await SearchNominatimAsync(normalized, limit, cancellationToken);
        }

        if (!admission.Succeeded || admission.Authority == null || !Matches(binding, admission.Authority)
            || !await _contactGate.IsCurrentAsync(admission.Authority, cancellationToken))
        {
            return TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.ProviderUnavailable);
        }

        var providerResult = await _geoapify!.SearchAsync(normalized, limit, admission.Authority.Credential, cancellationToken);
        if (providerResult.Status != TripEditorGeocodeProviderStatus.Success || providerResult.Response == null)
        {
            return MapFailure(providerResult.Status);
        }

        if (!await _contactGate.IsCurrentAsync(admission.Authority, cancellationToken))
        {
            return TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.ProviderUnavailable);
        }

        _cache.Set(cacheKey, providerResult.Response, CacheLifetime());
        return TripEditorGeocodeSearchOutcome.Success(providerResult.Response);
    }

    private async Task<TripEditorGeocodeSearchOutcome> SearchNominatimAsync(
        string normalized, int limit, CancellationToken cancellationToken)
    {
        var cacheKey = $"trip-editor-geocode:nominatim:jsonv2:en:{normalized}:{limit}";
        if (_cache.TryGetValue(cacheKey, out EditorGeocodeSearchResponseDto? cached) && cached != null)
        {
            return TripEditorGeocodeSearchOutcome.Success(cached);
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, _options.MinimumIntervalMilliseconds));
        if (!_rateLimiter.TryAcquire(interval))
        {
            return TripEditorGeocodeSearchOutcome.Failure(TripEditorGeocodeSearchStatus.LocalRateLimited);
        }

        var providerResult = await _nominatim.SearchAsync(normalized, limit, cancellationToken);
        if (providerResult.Status != TripEditorGeocodeProviderStatus.Success || providerResult.Response == null)
        {
            return MapFailure(providerResult.Status);
        }

        _cache.Set(cacheKey, providerResult.Response, CacheLifetime());
        return TripEditorGeocodeSearchOutcome.Success(providerResult.Response);
    }

    private TimeSpan CacheLifetime() => TimeSpan.FromSeconds(Math.Max(1, _options.CacheSeconds));

    private static TripEditorGeocodeSearchOutcome MapFailure(TripEditorGeocodeProviderStatus status) =>
        TripEditorGeocodeSearchOutcome.Failure(status switch
        {
            TripEditorGeocodeProviderStatus.RateLimited => TripEditorGeocodeSearchStatus.ProviderRateLimited,
            TripEditorGeocodeProviderStatus.Timeout => TripEditorGeocodeSearchStatus.ProviderTimeout,
            TripEditorGeocodeProviderStatus.Malformed => TripEditorGeocodeSearchStatus.ProviderMalformed,
            _ => TripEditorGeocodeSearchStatus.ProviderUnavailable
        });

    private static bool Matches(PersonalProviderAuthorityBinding binding, PersonalProviderAuthoritySnapshot snapshot) =>
        binding.ProviderKey == snapshot.ProviderKey && binding.ProfileId == snapshot.ProfileId
        && binding.CredentialGeneration == snapshot.CredentialGeneration
        && binding.CapabilityGeneration == snapshot.CapabilityGeneration
        && binding.SelectionGeneration == snapshot.SelectionGeneration
        && binding.Verification == snapshot.Verification
        && binding.VerifiedCredentialGeneration == snapshot.VerifiedCredentialGeneration
        && binding.VerifiedCapabilityGeneration == snapshot.VerifiedCapabilityGeneration;

    private static string BuildGeoapifyCacheKey(string userId, string query, int limit, PersonalProviderAuthorityBinding binding) =>
        $"trip-editor-geocode:{userId}:geoapify:json:en:{query}:{limit}:{binding.ProfileId}:{binding.CredentialGeneration}:{binding.CapabilityGeneration}:{binding.SelectionGeneration}:{binding.Verification}:{binding.VerifiedCredentialGeneration}:{binding.VerifiedCapabilityGeneration}";

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Public Nominatim-backed geocode search provider.
/// </summary>
public sealed class NominatimTripEditorGeocodeProvider : ITripEditorGeocodeProvider
{
    private const int ResponseLimit = 256 * 1024;
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

            if (response.Content.Headers.ContentLength > ResponseLimit)
            {
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed);
            }

            var payload = await ReadBoundedAsync(response.Content, cancellationToken);
            return TripEditorGeocodeProviderResult.Success(new EditorGeocodeSearchResponseDto(
                query,
                Attribution,
                ParseResults(payload)));
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

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > ResponseLimit) throw new JsonException("Response exceeds the bounded size.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IReadOnlyList<EditorGeocodeSearchResultDto> ParseResults(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Nominatim search response must be an array.");
        }

        var results = new List<EditorGeocodeSearchResultDto>();
        foreach (var element in document.RootElement.EnumerateArray().Take(6))
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new JsonException("Nominatim result must be an object.");
            var displayName = RequiredString(element, "display_name");
            var latitude = double.Parse(RequiredString(element, "lat"), CultureInfo.InvariantCulture);
            var longitude = double.Parse(RequiredString(element, "lon"), CultureInfo.InvariantCulture);
            if (!double.IsFinite(latitude) || latitude is < -90 or > 90
                || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            {
                throw new FormatException("Nominatim coordinates must be finite.");
            }

            var name = OptionalString(element, "name", 512) ?? displayName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? displayName;
            var id = OptionalString(element, "place_id") ??
                string.Join(':', new[] { OptionalString(element, "osm_type"), OptionalString(element, "osm_id") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            results.Add(new EditorGeocodeSearchResultDto(
                string.IsNullOrWhiteSpace(id) ? $"{ProviderName}:{latitude.ToString(CultureInfo.InvariantCulture)}:{longitude.ToString(CultureInfo.InvariantCulture)}" : $"{ProviderName}:{id}",
                ProviderName,
                name,
                displayName,
                BuildAddress(element) ?? displayName,
                OptionalString(element, "category", 128),
                OptionalString(element, "type", 128),
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
        OptionalString(element, propertyName, 512) ?? throw new JsonException($"Nominatim result missing {propertyName}.");

    private static string? OptionalString(JsonElement element, string propertyName, int maximum = 512)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new JsonException("Unexpected Nominatim field shape.");
        var text = value.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maximum ? text : text[..maximum];
    }
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
