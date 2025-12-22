using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>
/// Controller for viewing and managing application log files.
/// Provides file listing, content streaming, search, and download functionality.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "Admin")]
public class LogsController : BaseController
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;

    public LogsController(
        ILogger<LogsController> logger,
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        IWebHostEnvironment env)
        : base(logger, dbContext)
    {
        _configuration = configuration;
        _env = env;
    }

    /// <summary>
    /// Displays the log viewer page with available log files.
    /// </summary>
    /// <returns>View with list of log files.</returns>
    public IActionResult Index()
    {
        SetPageTitle("Application Logs");
        var logFiles = GetLogFiles();
        return View(logFiles);
    }

    /// <summary>
    /// Retrieves log file content with position tracking for navigation and polling.
    /// Supports tail-mode, forward reading, and backward reading for pagination.
    /// </summary>
    /// <param name="fileName">Name of the log file to read.</param>
    /// <param name="position">File position for navigation (start position for forward, end position for backward).</param>
    /// <param name="maxLines">Maximum number of lines to return.</param>
    /// <param name="mode">Read mode: "tail" (last N lines), "forward" (from position), "backward" (N lines before position).</param>
    /// <returns>JSON with content, positions, and navigation metadata.</returns>
    [HttpGet]
    public async Task<IActionResult> GetLogContent(string fileName, long position = 0, int maxLines = 1000, string mode = "tail")
    {
        if (!IsValidLogFile(fileName))
        {
            return BadRequest(new { success = false, message = "Invalid file name" });
        }

        var logPath = Path.Combine(GetLogDirectory(), fileName);
        if (!System.IO.File.Exists(logPath))
        {
            return NotFound(new { success = false, message = "Log file not found" });
        }

        var response = new LogContentResponse();
        var lines = new List<string>();

        try
        {
            await using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Capture file size at start to avoid race condition with concurrent writes.
            // This ensures any lines appended during our read will be picked up by the next poll.
            var initialFileSize = fs.Length;
            response.FileSize = initialFileSize;

            switch (mode.ToLowerInvariant())
            {
                case "tail":
                    // Read last N lines from end of file
                    (lines, response.StartPosition) = await ReadLastLinesWithPositionAsync(fs, maxLines);
                    response.NewPosition = initialFileSize;
                    response.HasMore = false;
                    response.HasOlder = response.StartPosition > 0;
                    break;

                case "backward":
                    // Read N lines before the given position (for "Previous" button)
                    if (position <= 0)
                    {
                        // Already at start, return empty
                        response.StartPosition = 0;
                        response.NewPosition = 0;
                        response.HasOlder = false;
                        response.HasMore = initialFileSize > 0;
                        break;
                    }
                    (lines, response.StartPosition) = await ReadLinesBeforePositionAsync(fs, position, maxLines);
                    response.NewPosition = position;
                    response.HasOlder = response.StartPosition > 0;
                    response.HasMore = position < initialFileSize;
                    break;

                case "forward":
                default:
                    // Read from position forward (for polling and "Next" button)
                    response.StartPosition = position;
                    var newlineLen = await DetectNewlineLengthAsync(fs);
                    fs.Seek(position, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fs, leaveOpen: true))
                    {
                        var lineCount = 0;
                        long bytesRead = 0;
                        string? line;
                        while (lineCount < maxLines && (line = await reader.ReadLineAsync()) != null)
                        {
                            lines.Add(line);
                            lineCount++;
                            bytesRead += System.Text.Encoding.UTF8.GetByteCount(line) + newlineLen;
                        }
                        response.NewPosition = position + bytesRead;
                    }
                    response.HasMore = response.NewPosition < initialFileSize;
                    response.HasOlder = position > 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading log file {FileName}", fileName);
            return StatusCode(500, new { success = false, message = "Error reading log file" });
        }

        response.Content = string.Join("\n", lines);
        response.LineCount = lines.Count;

        return Json(response);
    }

    /// <summary>
    /// Detects the line ending style (CRLF or LF) used in the file.
    /// Reads a small buffer from the start to find the first line ending.
    /// </summary>
    /// <param name="fs">File stream to analyze.</param>
    /// <returns>2 for CRLF (Windows), 1 for LF (Unix), defaults to 1 if no line ending found.</returns>
    private static async Task<int> DetectNewlineLengthAsync(FileStream fs)
    {
        var originalPosition = fs.Position;
        fs.Seek(0, SeekOrigin.Begin);

        var buffer = new byte[4096];
        var bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length);

        for (var i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == '\n')
            {
                fs.Seek(originalPosition, SeekOrigin.Begin);
                // Check if preceded by \r (CRLF)
                return (i > 0 && buffer[i - 1] == '\r') ? 2 : 1;
            }
        }

        fs.Seek(originalPosition, SeekOrigin.Begin);
        return 1; // Default to LF if no line ending found
    }

    /// <summary>
    /// Reads the last N lines from a file stream and returns the start position.
    /// Uses bounded backward scanning with incremental window expansion.
    /// </summary>
    /// <param name="fs">File stream to read from.</param>
    /// <param name="lineCount">Number of lines to read from the end.</param>
    /// <returns>Tuple of (lines, startPosition where content begins).</returns>
    private static async Task<(List<string> Lines, long StartPosition)> ReadLastLinesWithPositionAsync(FileStream fs, int lineCount)
    {
        if (fs.Length == 0)
        {
            return ([], 0);
        }

        var newlineLen = await DetectNewlineLengthAsync(fs);
        const int avgLineBytes = 180;
        var windowSize = (long)lineCount * avgLineBytes;

        while (true)
        {
            var startPosition = Math.Max(0, fs.Length - windowSize);

            fs.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, leaveOpen: true);

            // If we didn't start at beginning, skip partial first line
            if (startPosition > 0)
            {
                var partialLine = await reader.ReadLineAsync();
                startPosition += System.Text.Encoding.UTF8.GetByteCount(partialLine ?? "") + newlineLen;
            }

            // Read lines from current position to end
            var lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lines.Add(line);
            }

            // Return if we have enough lines or we've reached the start of file
            if (lines.Count >= lineCount || startPosition == 0)
            {
                if (lines.Count > lineCount)
                {
                    // Calculate how many lines to skip and adjust start position
                    var skipCount = lines.Count - lineCount;
                    for (var i = 0; i < skipCount; i++)
                    {
                        startPosition += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + newlineLen;
                    }
                    return (lines.Skip(skipCount).ToList(), startPosition);
                }
                return (lines, startPosition);
            }

            // Double the window for next iteration
            windowSize *= 2;
        }
    }

    /// <summary>
    /// Reads N lines before a given position in the file.
    /// Used for backward navigation (Previous button).
    /// </summary>
    /// <param name="fs">File stream to read from.</param>
    /// <param name="endPosition">Position to read before.</param>
    /// <param name="lineCount">Number of lines to read.</param>
    /// <returns>Tuple of (lines, startPosition where content begins).</returns>
    private static async Task<(List<string> Lines, long StartPosition)> ReadLinesBeforePositionAsync(FileStream fs, long endPosition, int lineCount)
    {
        if (endPosition <= 0)
        {
            return ([], 0);
        }

        var newlineLen = await DetectNewlineLengthAsync(fs);
        const int avgLineBytes = 180;
        var windowSize = (long)lineCount * avgLineBytes;

        while (true)
        {
            var startPosition = Math.Max(0, endPosition - windowSize);

            fs.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, leaveOpen: true);

            // If we didn't start at beginning, skip partial first line
            long actualStart = startPosition;
            if (startPosition > 0)
            {
                var partialLine = await reader.ReadLineAsync();
                actualStart += System.Text.Encoding.UTF8.GetByteCount(partialLine ?? "") + newlineLen;
            }

            // Read lines until we reach endPosition
            var lines = new List<string>();
            long currentPos = actualStart;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + newlineLen;
                if (currentPos + lineBytes > endPosition)
                {
                    break;
                }
                lines.Add(line);
                currentPos += lineBytes;
            }

            // Return if we have enough lines or we've reached the start of file
            if (lines.Count >= lineCount || startPosition == 0)
            {
                if (lines.Count > lineCount)
                {
                    // Take only the last N lines and adjust start position
                    var skipCount = lines.Count - lineCount;
                    for (var i = 0; i < skipCount; i++)
                    {
                        actualStart += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + newlineLen;
                    }
                    return (lines.Skip(skipCount).ToList(), actualStart);
                }
                return (lines, actualStart);
            }

            // Double the window for next iteration
            windowSize *= 2;
        }
    }

    /// <summary>
    /// Reads the last N lines from a file stream using bounded backward scanning.
    /// Expands the read window incrementally (doubling each time) until enough lines
    /// are found or the start of file is reached. Never reads the entire file at once
    /// to prevent OOM on large log files.
    /// </summary>
    /// <param name="fs">File stream to read from.</param>
    /// <param name="lineCount">Number of lines to read from the end.</param>
    /// <returns>List of the last N lines (or fewer if file has less than N lines).</returns>
    private static async Task<List<string>> ReadLastLinesAsync(FileStream fs, int lineCount)
    {
        if (fs.Length == 0)
        {
            return [];
        }

        // Start with estimated window based on average log line size
        const int avgLineBytes = 180;
        var windowSize = (long)lineCount * avgLineBytes;

        // Expand window until we have enough lines or reach file start
        while (true)
        {
            var startPosition = Math.Max(0, fs.Length - windowSize);

            fs.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, leaveOpen: true);

            // If we didn't start at beginning, skip partial first line
            if (startPosition > 0)
            {
                await reader.ReadLineAsync();
            }

            // Read lines from current position to end
            var lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lines.Add(line);
            }

            // Return if we have enough lines or we've reached the start of file
            if (lines.Count >= lineCount || startPosition == 0)
            {
                return lines.Count > lineCount
                    ? lines.Skip(lines.Count - lineCount).ToList()
                    : lines;
            }

            // Double the window for next iteration
            windowSize *= 2;
        }
    }

    /// <summary>
    /// Downloads a log file.
    /// </summary>
    /// <param name="fileName">Name of the log file to download.</param>
    /// <returns>File download response.</returns>
    [HttpGet]
    public IActionResult DownloadLog(string fileName)
    {
        if (!IsValidLogFile(fileName))
        {
            return BadRequest("Invalid file name");
        }

        var logPath = Path.Combine(GetLogDirectory(), fileName);
        if (!System.IO.File.Exists(logPath))
        {
            return NotFound("Log file not found");
        }

        var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return File(fs, "application/octet-stream", fileName);
    }

    /// <summary>
    /// Searches within a log file for matching lines.
    /// </summary>
    /// <param name="fileName">Name of the log file to search.</param>
    /// <param name="query">Search query string.</param>
    /// <returns>JSON with matching lines and count.</returns>
    [HttpGet]
    public async Task<IActionResult> SearchLogs(string fileName, string query)
    {
        if (!IsValidLogFile(fileName))
        {
            return BadRequest(new { success = false, message = "Invalid file name" });
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { success = false, message = "Search query is required" });
        }

        var logPath = Path.Combine(GetLogDirectory(), fileName);
        if (!System.IO.File.Exists(logPath))
        {
            return NotFound(new { success = false, message = "Log file not found" });
        }

        var matchingLines = new List<object>();
        var lineNumber = 0;

        try
        {
            await using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lineNumber++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matchingLines.Add(new { lineNumber, content = line });

                    // Limit results to prevent excessive memory usage
                    if (matchingLines.Count >= 500)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching log file {FileName}", fileName);
            return StatusCode(500, new { success = false, message = "Error searching log file" });
        }

        return Json(new
        {
            success = true,
            matchCount = matchingLines.Count,
            matches = matchingLines,
            truncated = matchingLines.Count >= 500
        });
    }

    /// <summary>
    /// Gets the log directory path based on configuration.
    /// </summary>
    /// <returns>Absolute path to the log directory.</returns>
    private string GetLogDirectory()
    {
        var logFilePath = _configuration["Logging:LogFilePath:Default"] ?? "Logs/wayfarer-.log";

        // Extract directory from the path pattern
        var directory = Path.GetDirectoryName(logFilePath) ?? "Logs";

        // If relative path, make it absolute based on content root
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(_env.ContentRootPath, directory);
        }

        return directory;
    }

    /// <summary>
    /// Gets list of available log files with metadata.
    /// </summary>
    /// <returns>List of log file info objects sorted by date descending.</returns>
    private List<LogFileInfo> GetLogFiles()
    {
        var logDirectory = GetLogDirectory();
        var logFiles = new List<LogFileInfo>();

        if (!Directory.Exists(logDirectory))
        {
            _logger.LogWarning("Log directory does not exist: {LogDirectory}", logDirectory);
            return logFiles;
        }

        var today = DateTime.Now.ToString("yyyyMMdd");

        foreach (var file in Directory.GetFiles(logDirectory, "wayfarer-*.log"))
        {
            var fileInfo = new FileInfo(file);
            logFiles.Add(new LogFileInfo
            {
                FileName = fileInfo.Name,
                SizeBytes = fileInfo.Length,
                SizeFormatted = FormatFileSize(fileInfo.Length),
                LastModified = fileInfo.LastWriteTime,
                IsCurrent = fileInfo.Name.Contains(today)
            });
        }

        return logFiles.OrderByDescending(f => f.LastModified).ToList();
    }

    /// <summary>
    /// Validates that a file name is a valid log file to prevent directory traversal attacks.
    /// </summary>
    /// <param name="fileName">File name to validate.</param>
    /// <returns>True if valid, false otherwise.</returns>
    private static bool IsValidLogFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // Prevent directory traversal
        if (fileName.Contains("..") || Path.IsPathRooted(fileName))
        {
            return false;
        }

        // Only allow log files matching our pattern
        return Regex.IsMatch(fileName, @"^wayfarer-\d{8}\.log$");
    }

    /// <summary>
    /// Formats file size in human-readable format.
    /// </summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>Formatted size string (e.g., "1.5 MB").</returns>
    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
