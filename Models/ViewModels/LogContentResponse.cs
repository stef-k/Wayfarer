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
    /// The new file position after reading (used for incremental reads).
    /// </summary>
    public long NewPosition { get; set; }

    /// <summary>
    /// Indicates if there is more content available beyond maxLines.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// The number of lines in the content.
    /// </summary>
    public int LineCount { get; set; }
}
