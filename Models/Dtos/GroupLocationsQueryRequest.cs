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
}

