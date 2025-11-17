namespace Wayfarer.Models.Dtos;

/// <summary>
/// DTO for trip data returned by the API
/// </summary>
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

    /// <summary>
    /// Tags associated with this trip for categorization
    /// </summary>
    public List<ApiTagDto>? Tags { get; set; }
}

/// <summary>
/// DTO for tag data
/// </summary>
public class ApiTagDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}