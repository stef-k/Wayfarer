using System.Globalization;
using System.Net;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Performs explicit bounded Geoapify capability verification with no selection side effect.</summary>
public sealed class GeoapifyVerificationService(
    HttpClient httpClient, PersonalProviderContactGate contactGate, ApplicationDbContext dbContext)
{
    private readonly ApplicationDbContext authorityContext = dbContext;
    private const int ResponseLimit = 262_144;

    /// <summary>Verifies the fixed non-personal reverse-geocoding request.</summary>
    public Task<PersonalProviderVerification> VerifyGeocodingAsync(
        string userId, CancellationToken cancellationToken = default) => VerifyAsync(
            userId, PersonalProviderCapability.Geocoding, BuildGeocodingUri, ValidateGeocoding, cancellationToken);

    /// <summary>Verifies the fixed non-personal one-pair walk routing request.</summary>
    public Task<PersonalProviderVerification> VerifyRoutingAsync(
        string userId, CancellationToken cancellationToken = default) => VerifyAsync(
            userId, PersonalProviderCapability.Routing, BuildRoutingUri, ValidateRouting, cancellationToken);

    private async Task<PersonalProviderVerification> VerifyAsync(string userId, PersonalProviderCapability capability,
        Func<string, Uri> uriFactory, Func<JsonElement, bool> validator, CancellationToken cancellationToken)
    {
        var admission = await contactGate.AdmitGeoapifyVerificationAsync(userId, capability, cancellationToken);
        if (!admission.Succeeded || admission.Authority == null)
            return PersonalProviderVerification.Unavailable;
        var authority = admission.Authority;
        if (!IsTrackedAuthorityCurrent(authority)
            || !await contactGate.IsGeoapifyVerificationCurrentAsync(authority, cancellationToken))
            return PersonalProviderVerification.Unavailable;

        var result = PersonalProviderVerification.Unavailable;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uriFactory(authority.Credential));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                result = PersonalProviderVerification.Failed;
            else if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength <= ResponseLimit)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length <= ResponseLimit)
                {
                    using var document = JsonDocument.Parse(bytes);
                    result = validator(document.RootElement)
                        ? PersonalProviderVerification.Verified : PersonalProviderVerification.Unavailable;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { }

        if (!IsTrackedAuthorityCurrent(authority)
            || !await contactGate.IsGeoapifyVerificationCurrentAsync(authority, cancellationToken))
            return PersonalProviderVerification.Unavailable;
        return await contactGate.TryRecordGeoapifyVerificationAsync(authority, result, cancellationToken)
            ? result : PersonalProviderVerification.Unavailable;
    }

    private bool IsTrackedAuthorityCurrent(PersonalProviderAuthoritySnapshot authority)
    {
        var tracked = authorityContext.ChangeTracker.Entries<PersonalLocationProviderProfile>()
            .Select(entry => entry.Entity).SingleOrDefault(profile =>
                profile.UserId == authority.UserId && profile.ProviderKey == authority.ProviderKey);
        return tracked == null || tracked.CredentialGeneration == authority.CredentialGeneration
            && (authority.Capability == PersonalProviderCapability.Geocoding
                ? tracked.GeocodingGeneration : tracked.RoutingGeneration) == authority.CapabilityGeneration;
    }

    private static Uri BuildGeocodingUri(string credential) => new(
        "https://api.geoapify.com/v1/geocode/reverse?lat=0&lon=0&format=geojson&lang=en&limit=1&apiKey="
        + Uri.EscapeDataString(credential));

    private static Uri BuildRoutingUri(string credential) => new(
        "https://api.geoapify.com/v1/routing?waypoints=0,0%7C0,0.01&mode=walk&format=json&lang=en"
        + "&details=instruction_details&type=balanced&traffic=free_flow&apiKey=" + Uri.EscapeDataString(credential));

    private static bool ValidateGeocoding(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type) && type.GetString() == "FeatureCollection"
        && root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array;

    private static bool ValidateRouting(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() != 1) return false;
        var route = results[0];
        if (!NonNegativeFinite(route, "distance") || !NonNegativeFinite(route, "time")
            || !route.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array
            || legs.GetArrayLength() != 1 || !ValidateGeometry(route)) return false;
        var leg = legs[0];
        return leg.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
            && steps.GetArrayLength() > 0 && steps.EnumerateArray().All(ValidateStep);
    }

    private static bool ValidateGeometry(JsonElement route)
    {
        if (!route.TryGetProperty("geometry", out var geometry)
            || !geometry.TryGetProperty("type", out var type) || type.GetString() != "LineString"
            || !geometry.TryGetProperty("coordinates", out var points) || points.ValueKind != JsonValueKind.Array
            || points.GetArrayLength() < 2) return false;
        var parsed = points.EnumerateArray().Select(ParsePoint).ToArray();
        return parsed.All(point => point.HasValue)
            && Close(parsed[0]!.Value, (0d, 0d)) && Close(parsed[^1]!.Value, (0.01d, 0d));
    }

    private static (double Longitude, double Latitude)? ParsePoint(JsonElement point)
    {
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2
            || !point[0].TryGetDouble(out var longitude) || !point[1].TryGetDouble(out var latitude)
            || !double.IsFinite(longitude) || !double.IsFinite(latitude)
            || longitude is < -180 or > 180 || latitude is < -90 or > 90) return null;
        return (longitude, latitude);
    }

    private static bool ValidateStep(JsonElement step) => NonNegativeFinite(step, "distance")
        && NonNegativeFinite(step, "time")
        && step.TryGetProperty("from_index", out var from) && from.TryGetInt32(out var fromIndex) && fromIndex >= 0
        && step.TryGetProperty("to_index", out var to) && to.TryGetInt32(out var toIndex) && toIndex > fromIndex
        && step.TryGetProperty("instruction", out var instruction) && instruction.ValueKind == JsonValueKind.Object
        && instruction.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString())
        && instruction.TryGetProperty("type", out var type) && !string.IsNullOrWhiteSpace(type.GetString());

    private static bool NonNegativeFinite(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.TryGetDouble(out var parsed)
        && double.IsFinite(parsed) && parsed >= 0;

    private static bool Close((double Longitude, double Latitude) actual, (double Longitude, double Latitude) expected) =>
        Math.Abs(actual.Longitude - expected.Longitude) <= 0.00001
        && Math.Abs(actual.Latitude - expected.Latitude) <= 0.00001;
}
