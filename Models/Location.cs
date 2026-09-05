using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models
{
    public partial class Location
    {
        public int Id { get; set; }
        public required string UserId { get; set; }

        // Server-side timestamp (UTC) when the location was logged
        public DateTime Timestamp { get; set; }

        // Local timestamp transmitted from the gps app when the location was logged
        public DateTime LocalTimestamp { get; set; }

        /// <summary>
        /// Client-supplied idempotency key used to deduplicate location retries.
        /// </summary>
        public Guid? IdempotencyKey { get; set; }

        public required string TimeZoneId { get; set; } // Time zone ID (e.g., "America/New_York")

        // PostGIS Point type to store LonLat in a single field
        [Required]
        public required Point Coordinates { get; set; } // Spatial data replacing Latitude and Longitude

        public double? Accuracy { get; set; } // in meters
        public double? Altitude { get; set; } // in meters
        public double? Speed { get; set; }    // Speed in meters per second or km/h
        public string? LocationType { get; set; } // Optional: Home, work, etc.

        // Running, walking, cycling, driving, eating, cinema, etc.
        // Foreign Key to ActivityType
        public int? ActivityTypeId { get; set; }

        // Navigation property to ActivityType
        public ActivityType? ActivityType { get; set; }

        // Reverse Geocoding information
        public string? Address { get; set; }
        public string? FullAddress { get; set; }
    /// <summary>Independent Geoapify display line; never synthesized from address components.</summary>
        [MaxLength(500)] public string? ProviderAddressLine1 { get; set; }
        public string? AddressNumber { get; set; }
        public string? StreetName { get; set; }
        public string? PostCode { get; set; }
        public string? Place { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        /// <summary>Optional named feature returned with the retained reverse-geocoding result.</summary>
        [MaxLength(500)] public string? ResolvedFeatureName { get; set; }
        /// <summary>Optional normalized provider-neutral feature type.</summary>
        [MaxLength(32)] public string? ResolvedFeatureType { get; set; }
        /// <summary>Provider that supplied the retained reverse-geocoding enrichment.</summary>
        [MaxLength(24)] public string? ReverseGeocodingProvider { get; set; }
        /// <summary>Provider storage mode governing the retained enrichment.</summary>
        [MaxLength(16)] public string? ReverseGeocodingStorageMode { get; set; }
        /// <summary>UTC instant at which reverse-geocoding enrichment was persisted.</summary>
        public DateTimeOffset? ReverseGeocodedAt { get; set; }
        public string? Notes { get; set; }  // This could store plain text, HTML, or Markdown

        #region Metadata Fields

        /// <summary>
        /// How this record was created: "api-log", "api-checkin", "import", "queue-import".
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Whether location was manually invoked by user (true) or automatic (false).
        /// </summary>
        public bool? IsUserInvoked { get; set; }

        /// <summary>
        /// Location provider/sensors: "gps", "network", "fused", etc.
        /// </summary>
        public string? Provider { get; set; }

        /// <summary>
        /// Direction of travel in degrees (0-360). 0 = North, 90 = East.
        /// </summary>
        public double? Bearing { get; set; }

        /// <summary>
        /// Mobile app version that captured this location.
        /// </summary>
        public string? AppVersion { get; set; }

        /// <summary>
        /// Mobile app build number.
        /// </summary>
        public string? AppBuild { get; set; }

        /// <summary>
        /// Device model that captured this location.
        /// </summary>
        public string? DeviceModel { get; set; }

        /// <summary>
        /// OS version on the capturing device.
        /// </summary>
        public string? OsVersion { get; set; }

        /// <summary>
        /// Battery level (0-100) when location was captured.
        /// </summary>
        public int? BatteryLevel { get; set; }

        /// <summary>
        /// Whether device was charging when location was captured.
        /// </summary>
        public bool? IsCharging { get; set; }

        #endregion

    }
}
