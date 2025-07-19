namespace Wayfarer.Models.Dtos;

public class ApiTripSegmentDto
{
    public Guid Id { get; set; }
    public string Mode { get; set; } = "";
    public double? EstimatedDistanceKm { get; set; }
    public double? EstimatedDurationMinutes { get; set; }
    public string? Notes { get; set; }
    public int DisplayOrder { get; set; }

    public Guid? FromPlaceId { get; set; }
    public Guid? ToPlaceId { get; set; }

    public string? RouteJson { get; set; }
}