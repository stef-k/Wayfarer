namespace Wayfarer.Models.ViewModels;

/// <summary>
/// Represents metadata about a log file for the admin log viewer.
/// </summary>
public class LogFileInfo
{
    /// <summary>
    /// The name of the log file (e.g., "wayfarer-20251222.log").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The size of the log file in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Human-readable file size (e.g., "1.5 MB").
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// The last modification time of the log file.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Indicates if this is the current day's log file.
    /// </summary>
    public bool IsCurrent { get; set; }
}
