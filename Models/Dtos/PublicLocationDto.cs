using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

public class PublicLocationDto
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    
    public int? ClusterId { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime LocalTimestamp { get; set; }
    public required string Timezone { get; set; }
    public required Point Coordinates { get; set; }
    public double? Accuracy { get; set; }
    public double? Altitude { get; set; }
    public double? Speed { get; set; }
    public string? LocationType { get; set; }
    public string? ActivityType { get; set; }
    public int? ActivityTypeId { get; set; }
    public string? Address { get; set; }
    public string? FullAddress { get; set; }
    /// <summary>Independent Geoapify display line; never synthesized from address components.</summary>
    public string? ProviderAddressLine1 { get; set; }
    /// <summary>Retained house number, including ranges and leading zeroes.</summary>
    public string? AddressNumber { get; set; }
    /// <summary>Retained enrichment tuple used for Location presentation, not capture-origin verification.</summary>
    public string? ReverseGeocodingProvider { get; set; }
    public string? ReverseGeocodingStorageMode { get; set; }
    public DateTimeOffset? ReverseGeocodedAt { get; set; }
    public string? StreetName { get; set; }
    public string? PostCode { get; set; }
    public string? Place { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    /// <summary>Optional provider-returned detected feature name.</summary>
    public string? ResolvedFeatureName { get; set; }
    /// <summary>Optional normalized detected feature type.</summary>
    public string? ResolvedFeatureType { get; set; }
    public string? Notes { get; set; }

    // Additional fields
    public bool IsLatestLocation { get; set; }

    public double LocationTimeThresholdMinutes { get; set; }
    /// <summary>Copies the complete retained address contract for Location-only response projections.</summary>
    public PublicLocationDto WithAddress(Location location)
    {
        Address = location.Address;
        FullAddress = location.FullAddress;
        ProviderAddressLine1 = location.ProviderAddressLine1;
        AddressNumber = location.AddressNumber;
        ReverseGeocodingProvider = location.ReverseGeocodingProvider;
        ReverseGeocodingStorageMode = location.ReverseGeocodingStorageMode;
        ReverseGeocodedAt = location.ReverseGeocodedAt;
        StreetName = location.StreetName;
        PostCode = location.PostCode;
        Place = location.Place;
        Region = location.Region;
        Country = location.Country;
        ResolvedFeatureName = location.ResolvedFeatureName;
        ResolvedFeatureType = location.ResolvedFeatureType;
        return this;
    }
}
