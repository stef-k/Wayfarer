namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request for group viewport location query.
/// </summary>
public class GroupLocationsQueryRequest
{
    public double MinLng { get; set; }
    public double MinLat { get; set; }
    public double MaxLng { get; set; }
    public double MaxLat { get; set; }
    public double ZoomLevel { get; set; }
    public List<string>? UserIds { get; set; }
    // Optional chronological filters
    public string? DateType { get; set; } // "day" | "month" | "year"
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
}

