namespace Wayfarer.Models.Dtos;

/// <summary>
/// DTO for receiving location data from mobile clients via log-location and check-in endpoints.
/// </summary>
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

    #region Metadata Fields

    /// <summary>
    /// Source/origin of the location data (e.g., "mobile-log", "mobile-checkin").
    /// If not provided, backend defaults based on endpoint.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Whether this location was manually invoked by user (check-in) or automatic.
    /// For check-in endpoint, this is implicitly true.
    /// </summary>
    public bool? IsUserInvoked { get; set; }

    /// <summary>
    /// Location provider/sensors: "gps", "network", "fused", etc.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Direction of travel in degrees (0-360).
    /// </summary>
    public double? Bearing { get; set; }

    /// <summary>
    /// Mobile app version string.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Mobile app build number.
    /// </summary>
    public string? AppBuild { get; set; }

    /// <summary>
    /// Device model identifier.
    /// </summary>
    public string? DeviceModel { get; set; }

    /// <summary>
    /// Operating system version.
    /// </summary>
    public string? OsVersion { get; set; }

    /// <summary>
    /// Battery level (0-100) when captured.
    /// </summary>
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// Whether device was charging when captured.
    /// </summary>
    public bool? IsCharging { get; set; }

    #endregion
}
