using System.Text.Json;

namespace Wayfarer.Parsers
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Wayfarer.Areas.Api.Controllers;

    // Root response that mirrors the FeatureCollection
    public class ReverseLocationResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("features")]
        public List<Feature> Features { get; set; }

        [JsonPropertyName("attribution")]
        public string Attribution { get; set; }
    }

    // Each Feature object in the features array
    public class Feature
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("geometry")]
        public Geometry Geometry { get; set; }

        [JsonPropertyName("properties")]
        public FeatureProperties Properties { get; set; }
    }

    // Geometry information (for a point, an array of [longitude, latitude])
    public class Geometry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("coordinates")]
        public List<double> Coordinates { get; set; }
    }

    // Properties that are directly under the "properties" node for each feature
    public class FeatureProperties
    {
        [JsonPropertyName("mapbox_id")]
        public string MapboxId { get; set; }

        [JsonPropertyName("feature_type")]
        public string FeatureType { get; set; }

        [JsonPropertyName("full_address")]
        public string FullAddress { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("name_preferred")]
        public string NamePreferred { get; set; }

        // Detailed coordinates information if needed
        [JsonPropertyName("coordinates")]
        public CoordinatesDetail Coordinates { get; set; }

        [JsonPropertyName("place_formatted")]
        public string PlaceFormatted { get; set; }

        // The nested context with additional details like address, street, postcode, etc.
        [JsonPropertyName("context")]
        public Context Context { get; set; }
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
        public string Accuracy { get; set; }

        [JsonPropertyName("routable_points")]
        public List<RoutablePoint> RoutablePoints { get; set; }
    }

    public class RoutablePoint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

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
        public ContextAddress Address { get; set; }

        [JsonPropertyName("street")]
        public ContextDetail Street { get; set; }

        [JsonPropertyName("postcode")]
        public ContextDetail Postcode { get; set; }

        [JsonPropertyName("locality")]
        public ContextDetail Locality { get; set; }

        [JsonPropertyName("place")]
        public ContextDetail Place { get; set; }

        [JsonPropertyName("district")]
        public ContextDetail District { get; set; }

        [JsonPropertyName("region")]
        public ContextDetail Region { get; set; }

        [JsonPropertyName("country")]
        public ContextDetail Country { get; set; }
    }

    // A basic context detail for most keys (e.g. street, postcode, place, region, country)
    public class ContextDetail
    {
        [JsonPropertyName("mapbox_id")]
        public string MapboxId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        // Additional fields can be added here if needed (e.g., wikidata_id)
    }

    // Specialized class for the "address" context that includes an address number and street name.
    public class ContextAddress : ContextDetail
    {
        [JsonPropertyName("address_number")]
        public string AddressNumber { get; set; }

        [JsonPropertyName("street_name")]
        public string StreetName { get; set; }
    }


    public class ReverseLocationResults
    {
        public string Address { get; set; } = string.Empty;
        public string FullAddress { get; set; } = string.Empty;
        public string AddressNumber { get; set; } = string.Empty;

        public string StreetName { get; set; } = string.Empty;
        public string Place { get; set; } = string.Empty;
        public string PostCode { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }


    public class ReverseGeocodingService
    {
        private readonly HttpClient _httpClient;
        private ILogger _logger;

        public ReverseGeocodingService(HttpClient httpClient, ILogger<BaseApiController> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<ReverseLocationResults> GetReverseGeocodingDataAsync(double latitude, double longitude, string apiToken, string provider = "Mapbox")
        {
            if (!provider.Equals("Mapbox", StringComparison.OrdinalIgnoreCase))
            {
                // Instead of throwing an exception, log the issue and return an empty object.
                _logger.LogWarning("Unsupported reverse geocoding provider.");
                return new ReverseLocationResults();
            }

            string url = $"https://api.mapbox.com/search/geocode/v6/reverse?limit=1&language=en&longitude={longitude}&latitude={latitude}&access_token={apiToken}";
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                // Log the error and return an empty result
                _logger.LogWarning("Failed to fetch reverse geocoding data.");
                return new ReverseLocationResults();
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();
            ReverseLocationResponse reverseData = JsonSerializer.Deserialize<ReverseLocationResponse>(jsonResponse);

            if (reverseData?.Features != null && reverseData.Features.Any())
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

                results.Address = feature.Properties.Context?.Street?.Name;
                results.FullAddress = feature.Properties.FullAddress;
                results.Place = feature.Properties.Context?.Place?.Name;
                results.AddressNumber = feature.Properties.Context?.Address?.AddressNumber;
                results.StreetName = feature.Properties.Context?.Address?.StreetName;
                results.PostCode = feature.Properties.Context?.Postcode?.Name;
                results.Region = feature.Properties.Context?.Region?.Name;
                results.Country = feature.Properties.Context?.Country?.Name;
            }

            return results;
        }

    }

}
