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
        public ActivityType ActivityType { get; set; }

        // Reverse Geocoding information
        public string? Address { get; set; }
        public string? FullAddress { get; set; }
        public string? AddressNumber { get; set; }
        public string? StreetName { get; set; }
        public string? PostCode { get; set; }
        public string? Place { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public string? Notes { get; set; }  // This could store plain text, HTML, or Markdown

        // Foreign key to Vehicle (Many Locations can belong to One Vehicle)
        // OPTIONAL if the app used by an Org to track workers/vehicles (Cascade delete enabled)
        public int? VehicleId { get; set; }    // Foreign key to Vehicle table, VehicleId is nullable (optional relation)

        public Vehicle? Vehicle { get; set; }  // Navigation property to Vehicle, Navigation property to Vehicle (nullable)
    }
}
