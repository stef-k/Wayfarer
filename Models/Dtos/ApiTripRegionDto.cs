namespace Wayfarer.Models.Dtos;

public class ApiTripRegionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public int DisplayOrder { get; set; }
    public string? CoverImageUrl { get; set; }
    public double[]? Center { get; set; }  // [lon, lat]

    public List<ApiTripPlaceDto>? Places { get; set; }
    public List<ApiTripAreaDto>? Areas { get; set; }
}