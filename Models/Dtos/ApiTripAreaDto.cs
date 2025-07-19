namespace Wayfarer.Models.Dtos;

public class ApiTripAreaDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public int? DisplayOrder { get; set; }
    public string? FillHex { get; set; }

    // GeoJSON field — must match mapper above
    public string? GeometryGeoJson { get; set; }
}