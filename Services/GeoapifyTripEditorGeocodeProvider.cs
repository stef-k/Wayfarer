using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>Performs one bounded Geoapify autocomplete contact with a protected personal credential.</summary>
public sealed class GeoapifyTripEditorGeocodeProvider(HttpClient httpClient)
{
    private const int ResponseLimit = 256 * 1024;
    private const string Provider = "geoapify";
    private const string Attribution = "Powered by Geoapify; data © OpenStreetMap contributors.";

    /// <summary>Searches the fixed English, unbiased Geoapify autocomplete endpoint.</summary>
    public async Task<TripEditorGeocodeProviderResult> SearchAsync(
        string query, int limit, string credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(query, limit, credential));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.RateLimited);
            if (!response.IsSuccessStatusCode)
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Unavailable);
            if (response.Content.Headers.ContentLength > ResponseLimit)
                return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed);

            var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
            return TripEditorGeocodeProviderResult.Success(new(query, Attribution, Parse(bytes, limit)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Timeout); }
        catch (HttpRequestException) { return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Unavailable); }
        catch (JsonException) { return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed); }
        catch (FormatException) { return TripEditorGeocodeProviderResult.Failure(TripEditorGeocodeProviderStatus.Malformed); }
    }

    private static Uri BuildUri(string query, int limit, string credential)
    {
        var builder = new UriBuilder("https://api.geoapify.com/v1/geocode/autocomplete");
        var values = new Dictionary<string, string>
        {
            ["text"] = query, ["format"] = "json", ["lang"] = "en",
            ["limit"] = Math.Clamp(limit, 1, 6).ToString(CultureInfo.InvariantCulture), ["apiKey"] = credential
        };
        builder.Query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > ResponseLimit) throw new JsonException("Response exceeds the bounded size.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IReadOnlyList<EditorGeocodeSearchResultDto> Parse(byte[] bytes, int requestedLimit)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Unexpected Geoapify response shape.");

        var normalized = new List<EditorGeocodeSearchResultDto>();
        foreach (var item in results.EnumerateArray().Take(Math.Clamp(requestedLimit, 1, 6)))
        {
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Unexpected Geoapify result shape.");
            var latitude = RequiredNumber(item, "lat");
            var longitude = RequiredNumber(item, "lon");
            if (!double.IsFinite(latitude) || latitude is < -90 or > 90
                || !double.IsFinite(longitude) || longitude is < -180 or > 180)
                throw new FormatException("Coordinates are invalid.");
            var display = RequiredString(item, "formatted", 512);
            var name = OptionalString(item, "name", 512) ?? display.Split(',', 2)[0].Trim();
            var placeId = OptionalString(item, "place_id", 256)
                ?? $"{latitude.ToString("R", CultureInfo.InvariantCulture)}:{longitude.ToString("R", CultureInfo.InvariantCulture)}";
            normalized.Add(new($"{Provider}:{placeId}", Provider, name, display,
                BuildAddress(item) ?? display, OptionalString(item, "category", 128),
                OptionalString(item, "result_type", 128), latitude, longitude));
        }
        return normalized;
    }

    private static double RequiredNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : throw new JsonException("Required numeric field is missing.");

    private static string RequiredString(JsonElement item, string name, int maximum) =>
        OptionalString(item, name, maximum) ?? throw new JsonException("Required string field is missing.");

    private static string? OptionalString(JsonElement item, string name, int maximum)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Unexpected string field shape.");
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static string? BuildAddress(JsonElement item)
    {
        var parts = new[] { "address_line1", "address_line2", "city", "state", "country" }
            .Select(name => OptionalString(item, name, 512)).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var address = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(address) ? null : address[..Math.Min(address.Length, 512)];
    }
}
