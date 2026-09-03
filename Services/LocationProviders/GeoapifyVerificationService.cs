using System.Globalization;
using System.Net;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Performs explicit bounded Geoapify capability verification with no selection side effect.</summary>
public sealed class GeoapifyVerificationService(
    HttpClient httpClient, PersonalProviderContactGate contactGate, ApplicationDbContext dbContext)
{
    private readonly ApplicationDbContext authorityContext = dbContext;
    private const int ResponseLimit = 262_144;
    // Public points beside Paris Hotel de Ville provide a stable land address and short walkable street segment.
    private static readonly RouteCoordinate GeocodingProbe = new(2.3522219, 48.856614);
    private static readonly RouteCoordinate[] RoutingProbe =
        [GeocodingProbe, new(2.353222, 48.856817)];

    /// <summary>Verifies the fixed non-personal reverse-geocoding request.</summary>
    public Task<GeoapifyVerificationOutcome> VerifyGeocodingAsync(
        string userId, CancellationToken cancellationToken = default) => VerifyAsync(
            userId, PersonalProviderCapability.Geocoding, BuildGeocodingUri, ValidateGeocoding, cancellationToken);

    /// <summary>Verifies the fixed non-personal one-pair walk routing request.</summary>
    public Task<GeoapifyVerificationOutcome> VerifyRoutingAsync(
        string userId, CancellationToken cancellationToken = default) => VerifyAsync(
            userId, PersonalProviderCapability.Routing, BuildRoutingUri, ValidateRouting, cancellationToken);

    private async Task<GeoapifyVerificationOutcome> VerifyAsync(string userId, PersonalProviderCapability capability,
        Func<string, Uri> uriFactory, Func<byte[], CancellationToken, Task<bool>> validator, CancellationToken cancellationToken)
    {
        var admission = await contactGate.AdmitGeoapifyVerificationAsync(userId, capability, cancellationToken);
        if (!admission.Succeeded || admission.Authority == null)
            return GeoapifyVerificationOutcome.PreContact(admission.Category);
        var authority = admission.Authority;
        if (!IsTrackedAuthorityCurrent(authority)
            || !await contactGate.IsGeoapifyVerificationCurrentAsync(authority, cancellationToken))
            return GeoapifyVerificationOutcome.AuthorityChanged();

        var result = PersonalProviderVerification.Unavailable;
        var category = GeoapifyVerificationCategory.TemporaryFailure;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uriFactory(authority.Credential));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { result = PersonalProviderVerification.Failed; category = GeoapifyVerificationCategory.ProviderRejected; }
            else if ((int)response.StatusCode == 429)
                category = GeoapifyVerificationCategory.RateLimited;
            else if (response.IsSuccessStatusCode)
            {
                var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
                if (bytes != null)
                {
                    result = await validator(bytes, cancellationToken)
                        ? PersonalProviderVerification.Verified : PersonalProviderVerification.Unavailable;
                    category = result == PersonalProviderVerification.Verified
                        ? GeoapifyVerificationCategory.Verified : GeoapifyVerificationCategory.InvalidResponse;
                }
                else category = GeoapifyVerificationCategory.InvalidResponse;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { category = GeoapifyVerificationCategory.InvalidResponse; }

        if (!IsTrackedAuthorityCurrent(authority)
            || !await contactGate.IsGeoapifyVerificationCurrentAsync(authority, cancellationToken))
            return GeoapifyVerificationOutcome.AuthorityChanged();
        return await contactGate.TryRecordGeoapifyVerificationAsync(authority, result, cancellationToken)
            ? new(result, category) : GeoapifyVerificationOutcome.AuthorityChanged();
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
        $"https://api.geoapify.com/v1/geocode/reverse?lat={GeocodingProbe.Latitude.ToString("R", CultureInfo.InvariantCulture)}&lon={GeocodingProbe.Longitude.ToString("R", CultureInfo.InvariantCulture)}&format=geojson&lang=en&limit=1&apiKey="
        + Uri.EscapeDataString(credential));

    private static Uri BuildRoutingUri(string credential) => new(
        "https://api.geoapify.com/" + GeoapifyRoutingAdapter.BuildRelativeRequest("walk", RoutingProbe, credential));

    private static Task<bool> ValidateGeocoding(byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var valid = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "FeatureCollection"
            && root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array
            && features.GetArrayLength() > 0 && features[0].ValueKind == JsonValueKind.Object
            && features[0].TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && HasText(properties, "formatted", "address_line1");
        return Task.FromResult(valid);
    }

    private static async Task<bool> ValidateRouting(byte[] bytes, CancellationToken cancellationToken)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return (await GeoapifyRoutingAdapter.ParseAsync(response, RoutingProbe, cancellationToken)).Succeeded;
    }

    private static bool HasText(JsonElement properties, params string[] names) => names.Any(name =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString()));

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > ResponseLimit) return null;
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (destination.Length <= ResponseLimit)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, ResponseLimit + 1L - destination.Length)), cancellationToken);
            if (read == 0) return destination.ToArray();
            destination.Write(buffer, 0, read);
        }
        return null;
    }
}

/// <summary>Identifies safe request-local Geoapify verification detail.</summary>
public enum GeoapifyVerificationCategory
{ Verified, AuthorizationDisabled, CredentialUnavailable, GuardExhausted, ProviderRejected, RateLimited, TemporaryFailure, InvalidResponse, AuthorityChanged }

/// <summary>Combines bounded persisted state with non-persisted actionable detail.</summary>
public sealed record GeoapifyVerificationOutcome(PersonalProviderVerification Verification, GeoapifyVerificationCategory Category)
{
    /// <summary>Maps a rejection that occurred before provider contact.</summary>
    public static GeoapifyVerificationOutcome PreContact(PersonalProviderAdmissionCategory category) => new(
        PersonalProviderVerification.Unverified, category switch
        {
            PersonalProviderAdmissionCategory.CredentialUnavailable => GeoapifyVerificationCategory.CredentialUnavailable,
            PersonalProviderAdmissionCategory.Exhausted => GeoapifyVerificationCategory.GuardExhausted,
            _ => GeoapifyVerificationCategory.AuthorizationDisabled
        });

    /// <summary>Returns a fail-closed stale-authority result.</summary>
    public static GeoapifyVerificationOutcome AuthorityChanged() =>
        new(PersonalProviderVerification.Unavailable, GeoapifyVerificationCategory.AuthorityChanged);
}
