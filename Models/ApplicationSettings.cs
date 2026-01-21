using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// Application settings, everything here is available to be updated at admin dashboard
/// </summary>
public class ApplicationSettings
{
    public const int DefaultMaxCacheTileSizeInMB = 1024;
    public const int DefaultMaxCacheMbtilesSizeInMB = 6144;
    public const int DefaultUploadSizeLimitMB = 100;
    public const string DefaultTileProviderKey = "osm";
    public const string DefaultTileProviderUrlTemplate = "https://a.tile.openstreetmap.org/{z}/{x}/{y}.png";
    public const string DefaultTileProviderAttribution = "&copy; OpenStreetMap contributors";
    public const int DefaultTileRateLimitPerMinute = 500;

    
    [Key]
    public int Id { get; set; } = 1;

    [Required]
    [Range(1, 1440, ErrorMessage = "Time threshold must be between 1 and 1440 minutes (24 hours).")]
    public int LocationTimeThresholdMinutes { get; set; } = 5;

    [Required]
    [Range(1, 100000, ErrorMessage = "Distance threshold must be between 1 and 100,000 meters.")]
    public int LocationDistanceThresholdMeters { get; set; } = 15;

    /// <summary>
    /// Maximum acceptable GPS accuracy in meters for location logging.
    /// Locations with accuracy worse (higher) than this value are rejected.
    /// This helps filter out unreliable GPS readings from poor signal conditions.
    /// Default is 50 meters.
    /// </summary>
    [Required]
    [Range(10, 1000, ErrorMessage = "Accuracy threshold must be between 10 and 1000 meters.")]
    public int LocationAccuracyThresholdMeters { get; set; } = 50;

    /// <summary>
    /// The max tile cache size (in MegaBytes [MB]) to store in file system for zoom levels >= 9.
    /// Zoom levels <= 8 are about 1 GB and have no relation to this setting.
    /// Tiles are calculated 10 - 20 KB each.
    /// Default is 1024 MB = 1 GB.
    /// </summary>
    [Required]
    [Range(-1, 102400, ErrorMessage = "Must be -1 (disable) or a positive size up to 100 GB.")]
    public int MaxCacheTileSizeInMB { get; set; } = 1024;

    /// <summary>
    /// Selected tile provider key (preset name or "custom").
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string TileProviderKey { get; set; } = DefaultTileProviderKey;

    /// <summary>
    /// Tile provider URL template with {z}/{x}/{y} placeholders and optional {apiKey}.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string TileProviderUrlTemplate { get; set; } = DefaultTileProviderUrlTemplate;

    /// <summary>
    /// Attribution HTML displayed in Leaflet maps for the active provider.
    /// </summary>
    [Required]
    [MaxLength(300)]
    public string TileProviderAttribution { get; set; } = DefaultTileProviderAttribution;

    /// <summary>
    /// Optional API key for paid tile providers or custom templates.
    /// </summary>
    [MaxLength(200)]
    public string? TileProviderApiKey { get; set; }

    /// <summary>
    /// Whether to rate limit anonymous tile requests to prevent abuse.
    /// Authenticated users are never rate limited.
    /// </summary>
    [Required]
    public bool TileRateLimitEnabled { get; set; } = true;

    /// <summary>
    /// Maximum tile requests per minute per IP address for anonymous users.
    /// Default is 500 which is generous for normal map viewing but blocks scrapers.
    /// </summary>
    [Required]
    [Range(50, 10000, ErrorMessage = "Rate limit must be between 50 and 10,000 requests per minute.")]
    public int TileRateLimitPerMinute { get; set; } = DefaultTileRateLimitPerMinute;

    /// <summary>
    /// Flag to control whether user registration is open or closed.
    /// </summary>
    [Required]
    public bool IsRegistrationOpen { get; set; } = false;
    
    // Application uploads file size limit in Megabytes, default is 100 MB
    [Required]
    [Range(-1, 102400)]
    public int UploadSizeLimitMB { get; set; } = 100;

    // === Trip Place Auto-Visited Settings ===

    /// <summary>
    /// Number of location pings required within the hit window to confirm a visit.
    /// </summary>
    [Required]
    [Range(2, 5, ErrorMessage = "Required hits must be between 2 and 5.")]
    public int VisitedRequiredHits { get; set; } = 2;

    /// <summary>
    /// Minimum detection radius in meters, used when GPS accuracy is good.
    /// </summary>
    [Required]
    [Range(10, 200, ErrorMessage = "Min radius must be between 10 and 200 meters.")]
    public int VisitedMinRadiusMeters { get; set; } = 35;

    /// <summary>
    /// Maximum detection radius in meters, ceiling for poor GPS accuracy.
    /// </summary>
    [Required]
    [Range(50, 500, ErrorMessage = "Max radius must be between 50 and 500 meters.")]
    public int VisitedMaxRadiusMeters { get; set; } = 100;

    /// <summary>
    /// Multiplier applied to reported GPS accuracy to calculate effective radius.
    /// </summary>
    [Required]
    [Range(0.5, 5.0, ErrorMessage = "Accuracy multiplier must be between 0.5 and 5.0.")]
    public double VisitedAccuracyMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Skip visit detection if GPS accuracy exceeds this value. Set to 0 to disable.
    /// </summary>
    [Required]
    [Range(0, 1000, ErrorMessage = "Accuracy reject must be between 0 and 1000 meters.")]
    public int VisitedAccuracyRejectMeters { get; set; } = 200;

    /// <summary>
    /// Maximum radius for PostGIS database query to find nearby places.
    /// </summary>
    [Required]
    [Range(50, 2000, ErrorMessage = "Search radius must be between 50 and 2000 meters.")]
    public int VisitedMaxSearchRadiusMeters { get; set; } = 150;

    /// <summary>
    /// Maximum HTML characters for notes snapshot when creating a visit event.
    /// </summary>
    [Required]
    [Range(1000, 200000, ErrorMessage = "Notes max chars must be between 1000 and 200000.")]
    public int VisitedPlaceNotesSnapshotMaxHtmlChars { get; set; } = 20000;

    /// <summary>
    /// Cooldown period in hours before sending another SSE notification for the same place.
    /// Set to -1 to completely disable visit notifications (never notify).
    /// Set to 0 to disable cooldown (always notify on every visit).
    /// Default is 24 hours - prevents notification spam when visiting the same place multiple times per day.
    /// </summary>
    [Required]
    [Range(-1, 720, ErrorMessage = "Notification cooldown must be -1 (never notify), 0 (always notify), or 1-720 hours.")]
    public int VisitNotificationCooldownHours { get; set; } = 24;

    // === Derived Properties (computed from LocationTimeThresholdMinutes) ===

    /// <summary>
    /// Time window in minutes for consecutive hit confirmation.
    /// Derived: LocationTimeThresholdMinutes × 1.6
    /// </summary>
    [NotMapped]
    public int VisitedHitWindowMinutes => (int)(LocationTimeThresholdMinutes * 1.6);

    /// <summary>
    /// Minutes after which stale candidates are cleaned up.
    /// Derived: LocationTimeThresholdMinutes × 12
    /// </summary>
    [NotMapped]
    public int VisitedCandidateStaleMinutes => LocationTimeThresholdMinutes * 12;

    /// <summary>
    /// Minutes of inactivity after which a visit is considered ended.
    /// Derived: LocationTimeThresholdMinutes × 9
    /// </summary>
    [NotMapped]
    public int VisitedEndVisitAfterMinutes => LocationTimeThresholdMinutes * 9;
}
