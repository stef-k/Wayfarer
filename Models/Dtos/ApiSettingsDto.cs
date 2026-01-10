namespace Wayfarer.Models.Dtos;

/// <summary>
/// Mobile app location filtering settings.
/// </summary>
public class ApiSettingsDto
{
    /// <summary>
    /// Ignore locations if they are less than this many minutes apart.
    /// </summary>
    public int LocationTimeThresholdMinutes { get; set; }

    /// <summary>
    /// Ignore locations if they are less than this many meters apart.
    /// </summary>
    public int LocationDistanceThresholdMeters { get; set; }

    /// <summary>
    /// Reject locations with GPS accuracy worse (higher) than this value in meters.
    /// </summary>
    public int LocationAccuracyThresholdMeters { get; set; }
}