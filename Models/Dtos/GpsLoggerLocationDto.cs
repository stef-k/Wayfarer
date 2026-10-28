namespace Wayfarer.Models.Dtos;

/// <summary>
/// DTO for receiving location data from mobile clients via log-location and check-in endpoints.
/// </summary>
public class GpsLoggerLocationDto
{
    /// <summary>Latitude supplied by the client; null cannot authorize a new observation.</summary>
    public double? Latitude { get; set; }
    /// <summary>Longitude supplied by the client; explicit zero is valid, absence is not zero.</summary>
    public double? Longitude { get; set; }
    public DateTime Timestamp { get; set; } // Local device time

    /// <summary>Optional coordinate origin: sensor, manual-map, or unknown; only sensor can establish presence.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(24)]
    public string? CoordinateOrigin { get; set; }

    /// <summary>Original sensor fix instant with an explicit UTC/offset representation.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(Wayfarer.Util.ObservationInstantJsonConverter))]
    public DateTimeOffset? ObservedAtUtc { get; set; }

    /// <summary>Known monotonic fix age at final submit; omit when reboot/clock change makes it uncertain.</summary>
    public long? ObservedAgeAtEventMs { get; set; }

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
    /// Check-in defaults to true, but explicitly queued automatic observations retain false.
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
