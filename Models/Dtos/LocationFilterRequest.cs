namespace Wayfarer.Models.Dtos;

public class LocationFilterRequest
{
    public double MinLongitude { get; set; }
    public double MinLatitude { get; set; }
    public double MaxLongitude { get; set; }
    public double MaxLatitude { get; set; }
    public double ZoomLevel { get; set; }
    
    public string? Username { get; set; }
}