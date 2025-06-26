namespace Wayfarer.Models.Dtos;

public class SegmentDto
{
    public Guid Id { get; set; }
    public string Mode { get; set; }
    public double? EstimatedDistanceKm { get; set; }
    public TimeSpan? EstimatedDuration { get; set; }
    public PlaceDto FromPlace { get; set; }
    public PlaceDto ToPlace { get; set; }
    
    public string? RouteJson { get; set; }
    public string Notes { get; set; }
}