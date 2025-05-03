using System.ComponentModel.DataAnnotations;

/// <summary>
/// Application settings, everything here is available to be updated at admin dashboard
/// </summary>
public class ApplicationSettings
{
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
    public int MaxCacheTileSizeInMB { get; set; } = 1024;
    
    /// <summary>
    /// Flag to control whether user registration is open or closed.
    /// </summary>
    [Required]
    public bool IsRegistrationOpen { get; set; } = false;
    
    // Application uploads file size limit in Megabytes, default is 100 MB
    [Required] 
    public int UploadSizeLimitMB { get; set; } = 100;
}
