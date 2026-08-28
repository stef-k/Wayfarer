using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

public class PlaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional detected feature name retained from reverse geocoding.</summary>
    public string? ResolvedFeatureName { get; set; }
    /// <summary>Optional normalized detected feature type.</summary>
    public string? ResolvedFeatureType { get; set; }
    public Point Location { get; set; } = null!;
}
