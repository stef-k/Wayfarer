namespace Wayfarer.Models.Dtos;

public class ApiTripDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public bool IsPublic { get; set; }
    public double? CenterLat { get; set; }
    public double? CenterLon { get; set; }
    public int? Zoom { get; set; }
    public string? CoverImageUrl { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ApiTripRegionDto>? Regions { get; set; }
    public List<ApiTripSegmentDto>? Segments { get; set; }
}