using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

public class PublicLocationDto
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    
    public int? ClusterId { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime LocalTimestamp { get; set; }
    public required string Timezone { get; set; }
    public required Point Coordinates { get; set; }
    public double? Accuracy { get; set; }
    public double? Altitude { get; set; }
    public double? Speed { get; set; }
    public string? LocationType { get; set; }
    public string? ActivityType { get; set; }
    public int? ActivityTypeId { get; set; }
    public string? Address { get; set; }
    public string? FullAddress { get; set; }
    public string? StreetName { get; set; }
    public string? PostCode { get; set; }
    public string? Place { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public string? Notes { get; set; }

    // Additional fields
    public bool IsLatestLocation { get; set; }

    public double LocationTimeThresholdMinutes { get; set; }
}
