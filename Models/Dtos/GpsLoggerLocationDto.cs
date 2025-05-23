namespace Wayfarer.Models.Dtos;

public class GpsLoggerLocationDto
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime Timestamp { get; set; } // Local device time

    public double? Accuracy { get; set; }
    public double? Altitude { get; set; }
    public double? Speed { get; set; }
    public string? LocationType { get; set; }
    public string? Notes { get; set; }

    public int? ActivityTypeId { get; set; }
    public int? VehicleId { get; set; }
}