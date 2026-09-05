using System.Globalization;
using System.Net;
using System.Text.Json;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Contacts and parses the fixed Geoapify persistent reverse-geocoding contract.</summary>
public sealed class GeoapifyReverseGeocodingAdapter(HttpClient httpClient)
{
    private const int ResponseLimit = 262_144;
    private const int ValueLimit = 500;

    /// <summary>Returns one complete normalized result or a bounded failure without leaking request data.</summary>
    public async Task<ReverseGeocodingResult> ReverseAsync(double latitude, double longitude, string credential,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidRequest);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(latitude, longitude, credential));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.Authorization);
            if ((int)response.StatusCode == 429)
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.RateLimited);
            if (!response.IsSuccessStatusCode)
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable);
            if (response.Content.Headers.ContentLength > ResponseLimit)
                return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > ResponseLimit)
                return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
            using var document = JsonDocument.Parse(bytes);
            return Parse(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable); }
        catch (HttpRequestException) { return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable); }
        catch (JsonException) { return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse); }
    }

    private static Uri BuildUri(double latitude, double longitude, string credential) => new(
        "https://api.geoapify.com/v1/geocode/reverse?lat=" + latitude.ToString("R", CultureInfo.InvariantCulture)
        + "&lon=" + longitude.ToString("R", CultureInfo.InvariantCulture)
        + "&format=geojson&lang=en&limit=1&apiKey=" + Uri.EscapeDataString(credential));

    private static ReverseGeocodingResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "FeatureCollection"
            || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array
            || features.GetArrayLength() == 0 || features[0].ValueKind != JsonValueKind.Object
            || !features[0].TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);

        var formatted = Read(properties, "formatted");
        var line = Read(properties, "address_line1");
        var number = Read(properties, "housenumber");
        var street = Read(properties, "street");
        if (string.IsNullOrEmpty(formatted) && string.IsNullOrEmpty(line))
            return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
        // A street line never borrows provider display or feature text.
        var address = street == null ? string.Empty : Join(number, street)!;
        return ReverseGeocodingResult.Success(new ReverseLocationResults
        {
            FullAddress = formatted ?? line!, ProviderAddressLine1 = line, Address = address, AddressNumber = number ?? string.Empty,
            StreetName = street ?? string.Empty, PostCode = Read(properties, "postcode") ?? string.Empty,
            Place = First(properties, "city", "town", "village"),
            Region = Read(properties, "state"),
            Country = Read(properties, "country") ?? string.Empty,
            ResolvedFeatureName = ResolvedFeatureMetadata.NormalizeName(ReadOptionalString(properties, "name")),
            ResolvedFeatureType = ResolvedFeatureMetadata.NormalizeGeoapifyType(ReadOptionalString(properties, "result_type"))
        });
    }

    private static string? ReadOptionalString(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Read(JsonElement properties, string name)
    {
        if (!properties.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var trimmed = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= ValueLimit ? trimmed : trimmed[..ValueLimit];
    }

    private static string? First(JsonElement properties, params string[] names) =>
        names.Select(name => Read(properties, name)).FirstOrDefault(value => value != null);

    private static string? Join(string? number, string? street) => number == null ? street
        : street == null ? number : $"{street} {number}";
}
