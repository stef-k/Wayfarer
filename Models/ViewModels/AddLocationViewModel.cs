using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class AddLocationViewModel
    {
        public int? Id { get; set; } // For editing  

        /// <summary>
        /// Selected activity type identifier; optional because existing locations may have none.
        /// </summary>
        public int? SelectedActivityId { get; set; } // Maps to ActivityTypeId

        [BindNever]
        public List<SelectListItem>? ActivityTypes { get; set; } // For dropdown in the view

        [Required]
        public double Latitude { get; set; } // Used to construct Point

        [Required]
        public double Longitude { get; set; } // Used to construct Point

        [MaxLength(500)] public string? Address { get; set; }
        [MaxLength(500)] public string? FullAddress { get; set; }
        [MaxLength(500)] public string? AddressNumber { get; set; }
        [MaxLength(500)] public string? StreetName { get; set; }
        [MaxLength(500)] public string? PostCode { get; set; }
        [MaxLength(500)] public string? Place { get; set; }
        [MaxLength(500)] public string? Region { get; set; }
        [MaxLength(500)] public string? Country { get; set; }

        /// <summary>Provider tuple shown by GET and used to reject stale provider publication on POST.</summary>
        [MaxLength(24)] public string? OriginalReverseGeocodingProvider { get; set; }
        [MaxLength(16)] public string? OriginalReverseGeocodingStorageMode { get; set; }
        public DateTimeOffset? OriginalReverseGeocodedAt { get; set; }

        /// <summary>Read-only detected provider feature displayed alongside editable address values.</summary>
        /// <summary>Server-owned Location presentation metadata, separate from posted concurrency tokens.</summary>
        [BindNever] public string? ProviderAddressLine1 { get; set; }
        [BindNever] public bool IsGeoapifyAddress { get; set; }
        [BindNever] public string? ResolvedFeatureName { get; set; }
        [BindNever] public string? ResolvedFeatureType { get; set; }

        public DateTime LocalTimestamp { get; set; }

        public string? TimeZoneId { get; set; }

        public double? Accuracy { get; set; }
        public double? Altitude { get; set; }
        public double? Speed { get; set; }
        public string? Notes { get; set; }

        [BindNever]
        public string? UserId { get; set; } // Will be set in the controller

        /// <summary>
        /// Destination to redirect to when the user chooses Save &amp; Return.
        /// </summary>
        public string? ReturnUrl { get; set; }

        #region Capture Metadata (read-only display fields)

        /// <summary>
        /// Source of the location data (e.g., "api-log", "api-checkin", "queue-import").
        /// </summary>
        [BindNever]
        public string? Source { get; set; }

        /// <summary>
        /// Whether the location was manually triggered by the user.
        /// </summary>
        [BindNever]
        public bool? IsUserInvoked { get; set; }

        /// <summary>
        /// Location provider used (e.g., "gps", "network", "fused").
        /// </summary>
        [BindNever]
        public string? Provider { get; set; }

        /// <summary>
        /// Direction of travel in degrees (0-360).
        /// </summary>
        [BindNever]
        public double? Bearing { get; set; }

        /// <summary>
        /// Version of the app that captured the location.
        /// </summary>
        [BindNever]
        public string? AppVersion { get; set; }

        /// <summary>
        /// Build number of the app that captured the location.
        /// </summary>
        [BindNever]
        public string? AppBuild { get; set; }

        /// <summary>
        /// Device model that captured the location.
        /// </summary>
        [BindNever]
        public string? DeviceModel { get; set; }

        /// <summary>
        /// Operating system version of the device.
        /// </summary>
        [BindNever]
        public string? OsVersion { get; set; }

        /// <summary>
        /// Battery level (0-100) when the location was captured.
        /// </summary>
        [BindNever]
        public int? BatteryLevel { get; set; }

        /// <summary>
        /// Whether the device was charging when the location was captured.
        /// </summary>
        [BindNever]
        public bool? IsCharging { get; set; }

        /// <summary>
        /// Indicates whether any capture metadata is available for display.
        /// </summary>
        [BindNever]
        public bool HasCaptureMetadata =>
            !string.IsNullOrEmpty(Source) ||
            IsUserInvoked.HasValue ||
            !string.IsNullOrEmpty(Provider) ||
            Bearing.HasValue ||
            !string.IsNullOrEmpty(AppVersion) ||
            !string.IsNullOrEmpty(DeviceModel) ||
            BatteryLevel.HasValue;

        #endregion
    }

}
