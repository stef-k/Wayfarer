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
    /// Retrieves log file content with position tracking for incremental reads.
    /// </summary>
    /// <param name="fileName">Name of the log file to read.</param>
    /// <param name="lastPosition">File position to start reading from (for tailing).</param>
    /// <param name="maxLines">Maximum number of lines to return.</param>
    /// <returns>JSON with content, new position, and metadata.</returns>
    [HttpGet]
    public async Task<IActionResult> GetLogContent(string fileName, long lastPosition = 0, int maxLines = 1000)
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
            fs.Seek(lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            var lineCount = 0;
            string? line;
            while (lineCount < maxLines && (line = await reader.ReadLineAsync()) != null)
            {
                lines.Add(line);
                lineCount++;
            }

            response.NewPosition = fs.Position;
            response.HasMore = await reader.ReadLineAsync() != null;
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
