namespace Wayfarer.Models.Dtos;

public class ApiTripPlaceDto
{
    /// <summary>Authorized persisted enrichment provenance; no regenerated provider claims.</summary>
    public string? AddressEnrichmentProvider { get; set; }
    /// <summary>Retained provider storage mode.</summary>
    public string? AddressEnrichmentStorageMode { get; set; }
    /// <summary>Original enrichment instant, independent of Trip updates.</summary>
    public DateTimeOffset? AddressEnrichedAt { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public int? DisplayOrder { get; set; }
    public string? IconName { get; set; }
    public string? MarkerColor { get; set; }
    public string? Address { get; set; }
    /// <summary>Optional detected feature name retained from reverse geocoding.</summary>
    public string? ResolvedFeatureName { get; set; }
    /// <summary>Optional normalized detected feature type.</summary>
    public string? ResolvedFeatureType { get; set; }
    public double[]? Location { get; set; }  // [lon, lat]
}
