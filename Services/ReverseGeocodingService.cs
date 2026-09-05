using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Parsers
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Wayfarer.Areas.Api.Controllers;

    // Root response that mirrors the FeatureCollection
    public class ReverseLocationResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("features")]
        public List<Feature>? Features { get; set; }

        [JsonPropertyName("attribution")]
        public string Attribution { get; set; } = string.Empty;
    }

    // Each Feature object in the features array
    public class Feature
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("geometry")]
        public Geometry Geometry { get; set; } = null!;

        [JsonPropertyName("properties")]
        public FeatureProperties Properties { get; set; } = null!;
    }

    // Geometry information (for a point, an array of [longitude, latitude])
    public class Geometry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("coordinates")]
        public List<double> Coordinates { get; set; } = new();
    }

    // Properties that are directly under the "properties" node for each feature
    public class FeatureProperties
    {
        [JsonPropertyName("mapbox_id")]
        public string MapboxId { get; set; } = string.Empty;

        [JsonPropertyName("feature_type")]
        public string FeatureType { get; set; } = string.Empty;

        [JsonPropertyName("full_address")]
        public string FullAddress { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("name_preferred")]
        public string NamePreferred { get; set; } = string.Empty;

        // Detailed coordinates information if needed
        [JsonPropertyName("coordinates")]
        public CoordinatesDetail Coordinates { get; set; } = null!;

        [JsonPropertyName("place_formatted")]
        public string PlaceFormatted { get; set; } = string.Empty;

        // The nested context with additional details like address, street, postcode, etc.
        [JsonPropertyName("context")]
        public Context Context { get; set; } = new();
    }

    // Coordinates detail class (for the nested coordinates object)
    public class CoordinatesDetail
    {
        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        // Optional: you can add accuracy or routable_points if needed.
        [JsonPropertyName("accuracy")]
        public string Accuracy { get; set; } = string.Empty;

        [JsonPropertyName("routable_points")]
        public List<RoutablePoint> RoutablePoints { get; set; } = new();
    }

    public class RoutablePoint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }

    // The context object contains various related details.
    public class Context
    {
        // The "address" context is special because it contains an address number and a street name.
        [JsonPropertyName("address")]
        public ContextAddress Address { get; set; } = new();

        [JsonPropertyName("street")]
        public ContextDetail Street { get; set; } = new();

        [JsonPropertyName("postcode")]
        public ContextDetail Postcode { get; set; } = new();

        [JsonPropertyName("locality")]
        public ContextDetail Locality { get; set; } = new();

        [JsonPropertyName("place")]
        public ContextDetail Place { get; set; } = new();

        [JsonPropertyName("district")]
        public ContextDetail District { get; set; } = new();

        [JsonPropertyName("region")]
        public ContextDetail Region { get; set; } = new();

        [JsonPropertyName("country")]
        public ContextDetail Country { get; set; } = new();
    }

    // A basic context detail for most keys (e.g. street, postcode, place, region, country)
    public class ContextDetail
    {
        [JsonPropertyName("mapbox_id")]
        public string MapboxId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Additional fields can be added here if needed (e.g., wikidata_id)
    }

    // Specialized class for the "address" context that includes an address number and street name.
    public class ContextAddress : ContextDetail
    {
        [JsonPropertyName("address_number")]
        public string AddressNumber { get; set; } = string.Empty;

        [JsonPropertyName("street_name")]
        public string StreetName { get; set; } = string.Empty;
    }


    public class ReverseLocationResults
    {
        public string Address { get; set; } = string.Empty;
    /// <summary>Independent Geoapify display line; never synthesized from address components.</summary>
        public string? ProviderAddressLine1 { get; set; }
        public string FullAddress { get; set; } = string.Empty;
        public string AddressNumber { get; set; } = string.Empty;

        public string StreetName { get; set; } = string.Empty;
        public string? Place { get; set; }
        public string PostCode { get; set; } = string.Empty;
        public string? Region { get; set; }
        public string Country { get; set; } = string.Empty;
        public string? ResolvedFeatureName { get; set; }
        public string? ResolvedFeatureType { get; set; }
    }


    public class ReverseGeocodingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly PersonalProviderContactGate? _contactGate;
        private readonly ApplicationDbContext? _dbContext;
        private readonly GeoapifyReverseGeocodingAdapter _geoapify;

        public ReverseGeocodingService(HttpClient httpClient, ILogger<BaseApiController> logger,
            PersonalProviderContactGate? contactGate = null, ApplicationDbContext? dbContext = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _contactGate = contactGate;
            _dbContext = dbContext;
            _geoapify = new GeoapifyReverseGeocodingAdapter(httpClient);
        }

        /// <summary>Returns one generation-bound, admitted Permanent enrichment.</summary>
        public async Task<ReverseGeocodingResult> EnrichAsync(string userId, double latitude, double longitude,
            ReverseGeocodingIntent intent, CancellationToken cancellationToken = default)
        {
            if (_contactGate == null || !HasValidCoordinates(latitude, longitude))
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.InvalidRequest);
            var admission = await _contactGate.AdmitPersistentGeocodingAsync(userId, cancellationToken);
            if (!admission.Succeeded) return ReverseGeocodingResult.Unavailable(MapAdmission(admission.Category));
            var authority = admission.Authority!;
            if (!await _contactGate.IsCurrentAsync(authority, cancellationToken))
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.StaleAuthority);
            ReverseGeocodingResult result;
            try
            {
                result = authority.ProviderKey == "geoapify"
                    ? await _geoapify.ReverseAsync(latitude, longitude, authority.Credential, cancellationToken)
                    : await ContactAsync(latitude, longitude, authority.Credential, cancellationToken, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.CancelledAfterContact)
                    with { Authority = authority };
            }
            if (result.Category == ReverseGeocodingCategory.Authorization && authority.ProviderKey == "mapbox")
                await RecordMapboxAuthorizationFailureAsync(userId, cancellationToken);
            if (!result.Succeeded) return result with { Authority = authority };
            if (!await _contactGate.IsCurrentAsync(authority, cancellationToken))
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.StaleAuthority);
            return result with { Authority = authority };
        }

        /// <summary>Contacts only with an already-admitted authority; this transport owns no EF context.</summary>
        public async Task<ReverseGeocodingResult> ContactAdmittedAsync(
            PersonalProviderAuthoritySnapshot authority, double latitude, double longitude,
            CancellationToken cancellationToken = default)
        {
            if (!HasValidCoordinates(latitude, longitude))
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.InvalidRequest)
                    with { Authority = authority };
            try
            {
                var result = authority.ProviderKey == "geoapify"
                    ? await _geoapify.ReverseAsync(latitude, longitude, authority.Credential, cancellationToken)
                    : await ContactAsync(latitude, longitude, authority.Credential, cancellationToken, false);
                return result with { Authority = authority };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.CancelledAfterContact)
                    with { Authority = authority };
            }
        }

        /// <summary>Returns whether coordinates are finite and within the WGS 84 latitude/longitude bounds.</summary>
        internal static bool HasValidCoordinates(double latitude, double longitude) => double.IsFinite(latitude)
            && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

        /// <summary>Performs one explicit Permanent verification contact using fixed non-personal coordinates.</summary>
        public async Task<PersonalProviderVerification> VerifyMapboxPermanentAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (_contactGate == null || _dbContext == null) return PersonalProviderVerification.Unavailable;
            var admission = await _contactGate.AdmitMapboxPermanentVerificationAsync(userId, cancellationToken);
            if (!admission.Succeeded) return PersonalProviderVerification.Unavailable;
            var authority = admission.Authority!;
            if (!await _contactGate.IsVerificationCurrentAsync(authority, cancellationToken)) return PersonalProviderVerification.Unavailable;
            var result = await ContactAsync(0, 0, authority.Credential, cancellationToken, true);
            if (result.Category == ReverseGeocodingCategory.InvalidResponse) return PersonalProviderVerification.Unavailable;
            var state = result.Category == ReverseGeocodingCategory.Success ? PersonalProviderVerification.Verified
                : result.Category == ReverseGeocodingCategory.Authorization ? PersonalProviderVerification.Failed
                : PersonalProviderVerification.Unavailable;
            return await _contactGate.TryRecordMapboxPermanentVerificationAsync(authority, state, cancellationToken)
                ? state : PersonalProviderVerification.Unavailable;
        }

        private async Task RecordMapboxAuthorizationFailureAsync(string userId, CancellationToken cancellationToken)
        {
            if (_dbContext == null) return;
            var profile = await _dbContext.Set<PersonalLocationProviderProfile>().SingleOrDefaultAsync(
                item => item.UserId == userId && item.ProviderKey == "mapbox", cancellationToken);
            if (profile == null) return;
            profile.GeocodingVerification = PersonalProviderVerification.Failed;
            profile.GeocodingVerifiedCredentialGeneration = null;
            profile.GeocodingVerifiedConfigurationGeneration = null;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        /// <summary>Test-only transport parser retained for focused fake-HTTP parsing coverage.</summary>
        internal async Task<ReverseLocationResults> GetReverseGeocodingDataAsync(
            double latitude,
            double longitude,
            string apiToken,
            string provider = "Mapbox",
            CancellationToken cancellationToken = default)
        {
            if (!provider.Equals("Mapbox", StringComparison.OrdinalIgnoreCase))
            {
                // Instead of throwing an exception, log the issue and return an empty object.
                _logger.LogWarning("Unsupported reverse geocoding provider.");
                return new ReverseLocationResults();
            }

            string url = $"https://api.mapbox.com/search/geocode/v6/reverse?permanent=true&limit=1&language=en&longitude={longitude}&latitude={latitude}&access_token={apiToken}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Log the error and return an empty result
                _logger.LogWarning("Failed to fetch reverse geocoding data.");
                return new ReverseLocationResults();
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();
            ReverseLocationResponse? reverseData;
            try { reverseData = JsonSerializer.Deserialize<ReverseLocationResponse>(jsonResponse); }
            catch (JsonException) { return new ReverseLocationResults(); }

            if (HasValidEnvelope(reverseData) && reverseData!.Features!.Count != 0)
            {
                // Optionally choose a specific feature (e.g., "street") or just use the first one
                Feature feature = reverseData.Features
                                    .FirstOrDefault(f => f.Properties.FeatureType?.ToLower() == "street")
                                  ?? reverseData.Features.First();

                // Make sure your mapping method is implemented and returns a valid ReverseLocationResults object.
                ReverseLocationResults results = reverseLocationResults(reverseData);
                return results;
            }
            else
            {
                _logger.LogInformation("No features found in the JSON response.");
                return new ReverseLocationResults();
            }
        }

        private async Task<ReverseGeocodingResult> ContactAsync(double latitude, double longitude, string credential,
            CancellationToken cancellationToken, bool allowEmpty)
        {
            try
            {
                var url = $"https://api.mapbox.com/search/geocode/v6/reverse?permanent=true&limit=1&language=en&longitude={longitude}&latitude={latitude}&access_token={Uri.EscapeDataString(credential)}";
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.Authorization);
                if ((int)response.StatusCode == 429) return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.RateLimited);
                if (!response.IsSuccessStatusCode) return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable);
                if (response.Content.Headers.ContentLength > 262_144) return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > 262_144) return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
                var data = JsonSerializer.Deserialize<ReverseLocationResponse>(bytes);
                if (!HasValidEnvelope(data)) return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
                if (data!.Features!.Count == 0)
                    return allowEmpty ? ReverseGeocodingResult.Success(new())
                        : ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse);
                var value = reverseLocationResults(data);
                return string.IsNullOrWhiteSpace(value.FullAddress) && string.IsNullOrWhiteSpace(value.Address)
                    ? ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse)
                    : ReverseGeocodingResult.Success(value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable); }
            catch (HttpRequestException) { return ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.ProviderUnavailable); }
            catch (JsonException) { return ReverseGeocodingResult.Failure(ReverseGeocodingCategory.InvalidResponse); }
        }

        private static ReverseGeocodingCategory MapAdmission(PersonalProviderAdmissionCategory category) => category switch
        {
            PersonalProviderAdmissionCategory.NoProviderSelected => ReverseGeocodingCategory.NoProviderSelected,
            PersonalProviderAdmissionCategory.ConsentRequired => ReverseGeocodingCategory.ConsentRequired,
            PersonalProviderAdmissionCategory.Unauthorized => ReverseGeocodingCategory.Unauthorized,
            PersonalProviderAdmissionCategory.Unverified => ReverseGeocodingCategory.VerificationRequired,
            PersonalProviderAdmissionCategory.Exhausted => ReverseGeocodingCategory.Exhausted,
            PersonalProviderAdmissionCategory.CredentialUnavailable => ReverseGeocodingCategory.CredentialRequired,
            _ => ReverseGeocodingCategory.ProviderUnavailable
        };

        private static bool HasValidEnvelope(ReverseLocationResponse? data) =>
            data is { Type: "FeatureCollection", Features: not null }
            && data.Features.All(feature => feature != null && feature.Type == "Feature"
                && !string.IsNullOrWhiteSpace(feature.Id) && feature.Properties != null
                && !string.IsNullOrWhiteSpace(feature.Properties.FeatureType));


        private ReverseLocationResults reverseLocationResults(ReverseLocationResponse reverseData)
        {
            ReverseLocationResults results = new();

            if (reverseData?.Features != null && reverseData.Features.Any())
            {
                // Option 1: Choose the feature with feature_type "street" (if available)
                Feature feature = reverseData.Features
                                .FirstOrDefault(f => f.Properties.FeatureType?.ToLower() == "street")
                              // Option 2: Fallback to the first feature
                              ?? reverseData.Features.First();

                // Map the data into your custom results object.
                // Note: Some fields (like AddressNumber) are not available in this response.

                results.Address = feature.Properties.Context?.Street?.Name ?? string.Empty;
                results.FullAddress = feature.Properties.FullAddress ?? string.Empty;
                string? placeName = feature.Properties.Context?.Place?.Name;
                results.Place = string.IsNullOrWhiteSpace(placeName) ? string.Empty : placeName;
                results.AddressNumber = feature.Properties.Context?.Address?.AddressNumber ?? string.Empty;
                results.StreetName = feature.Properties.Context?.Address?.StreetName ?? string.Empty;
                results.PostCode = feature.Properties.Context?.Postcode?.Name ?? string.Empty;
                string? regionName = feature.Properties.Context?.Region?.Name;
                results.Region = string.IsNullOrWhiteSpace(regionName) ? string.Empty : regionName;
                results.Country = feature.Properties.Context?.Country?.Name ?? string.Empty;
            }

            return results;
        }

    }

    /// <summary>Identifies the durable caller's bounded enrichment purpose.</summary>
    public enum ReverseGeocodingIntent { LocationCreate, LocationCoordinateRefresh, ImportMissingAddress, PlaceAddress }

    /// <summary>Identifies bounded outcomes safe for callers and diagnostics.</summary>
    public enum ReverseGeocodingCategory
    { Success, InvalidRequest, CredentialRequired, NoProviderSelected, ConsentRequired, Unauthorized, VerificationRequired, Exhausted, Authorization, RateLimited, ProviderUnavailable, InvalidResponse, StaleAuthority, CancelledAfterContact }

    /// <summary>Contains normalized fields and internal generation-bound persistence authority.</summary>
    public sealed record ReverseGeocodingResult(ReverseGeocodingCategory Category, ReverseLocationResults? Value,
        PersonalProviderAuthoritySnapshot? Authority)
    {
        public bool Succeeded => Category == ReverseGeocodingCategory.Success && Value != null;
        public static ReverseGeocodingResult Success(ReverseLocationResults value) => new(ReverseGeocodingCategory.Success, value, null);
        public static ReverseGeocodingResult Unavailable(ReverseGeocodingCategory category) => new(category, null, null);
        public static ReverseGeocodingResult Failure(ReverseGeocodingCategory category) => new(category, null, null);

        /// <summary>Atomically applies a complete successful enrichment and provider-specific provenance.</summary>
        public bool ApplyTo(Location location, DateTimeOffset persistedAt)
        {
            if (!Succeeded || Value == null) return false;
            location.FullAddress = Value.FullAddress; location.Address = Value.Address;
            location.AddressNumber = Value.AddressNumber; location.StreetName = Value.StreetName;
            location.PostCode = Value.PostCode; location.Place = Value.Place;
            location.Region = Value.Region; location.Country = Value.Country;
            location.ProviderAddressLine1 = Value.ProviderAddressLine1;
            location.ResolvedFeatureName = Value.ResolvedFeatureName;
            location.ResolvedFeatureType = Value.ResolvedFeatureType;
            var provider = Authority?.ProviderKey ?? "mapbox";
            location.ReverseGeocodingProvider = provider;
            location.ReverseGeocodingStorageMode = provider == "geoapify" ? "persistent" : "permanent";
            location.ReverseGeocodedAt = persistedAt.ToUniversalTime();
            return true;
        }
    }

}
