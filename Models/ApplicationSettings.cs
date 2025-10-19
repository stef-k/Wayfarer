using System.ComponentModel.DataAnnotations;

/// <summary>
/// Application settings, everything here is available to be updated at admin dashboard
/// </summary>
public class ApplicationSettings
{
    public const int DefaultMaxCacheTileSizeInMB = 1024;
    public const int DefaultMaxCacheMbtilesSizeInMB = 6144;
    public const int DefaultUploadSizeLimitMB = 100;

    
    [Key]
    public int Id { get; set; } = 1;

    [Required]
    [Range(1, 60, ErrorMessage = "Time threshold must be between 1 and 60 minutes.")]
    public int LocationTimeThresholdMinutes { get; set; } = 5;

    [Required]
    [Range(1, 500, ErrorMessage = "Distance threshold must be between 1 and 500 meters.")]
    public int LocationDistanceThresholdMeters { get; set; } = 15;

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
    /// Flag to control whether user registration is open or closed.
    /// </summary>
    [Required]
    public bool IsRegistrationOpen { get; set; } = false;
    
    // Application uploads file size limit in Megabytes, default is 100 MB
    [Required] 
    [Range(-1, 102400)]
    public int UploadSizeLimitMB { get; set; } = 100;

    /// <summary>
    /// When enabled, automatically delete a group when its last active member leaves
    /// or is removed. Defaults to false (feature disabled).
    /// </summary>
    [Required]
    public bool AutoDeleteEmptyGroups { get; set; } = false;
    
}
