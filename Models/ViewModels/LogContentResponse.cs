namespace Wayfarer.Models.ViewModels;

/// <summary>
/// Response structure for streaming log file content with position tracking.
/// </summary>
public class LogContentResponse
{
    /// <summary>
    /// The log content retrieved from the file.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The file position where this content starts (for backward navigation).
    /// </summary>
    public long StartPosition { get; set; }

    /// <summary>
    /// The new file position after reading (used for incremental reads/forward navigation).
    /// </summary>
    public long NewPosition { get; set; }

    /// <summary>
    /// Total file size in bytes (for UI progress indication).
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Indicates if there is more content available beyond maxLines.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Indicates if there is older content available before StartPosition.
    /// </summary>
    public bool HasOlder { get; set; }

    /// <summary>
    /// The number of lines in the content.
    /// </summary>
    public int LineCount { get; set; }
}
