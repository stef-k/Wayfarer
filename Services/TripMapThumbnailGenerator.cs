using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>
/// Service for generating trip map thumbnails using Playwright screenshots.
/// Screenshots the public embed view of trips to create thumbnails that use the app's local tile cache.
/// </summary>
public sealed class TripMapThumbnailGenerator : ITripMapThumbnailGenerator
{
    // Static semaphore to prevent concurrent browser installations across all instances
    private static readonly SemaphoreSlim _installLock = new(1, 1);
    private static bool _browsersInstalled = false;

    private readonly ILogger<TripMapThumbnailGenerator> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly string _thumbsDirectory;
    private readonly string _chromeCachePath;
    private readonly Func<CancellationToken, Task<byte[]?>>? _captureOverride;

    /// <summary>
    /// Initializes the thumbnail generator.
    /// </summary>
    public TripMapThumbnailGenerator(
        ILogger<TripMapThumbnailGenerator> logger,
        IWebHostEnvironment env,
        IConfiguration configuration)
    {
        _logger = logger;
        _env = env;
        _configuration = configuration;

        // Prepare thumbs directory
        _thumbsDirectory = Path.Combine(_env.WebRootPath, "thumbs", "trips");
        Directory.CreateDirectory(_thumbsDirectory);
        _logger.LogInformation("Thumbnail directory: {ThumbsDirectory}", _thumbsDirectory);

        // Get Chrome cache directory from configuration (defaults to ChromeCache if not specified)
        _chromeCachePath = configuration["CacheSettings:ChromeCacheDirectory"] ?? "ChromeCache";
        _chromeCachePath = Path.GetFullPath(_chromeCachePath);

        // Configure Playwright to store browsers in ChromeCache directory
        var playwrightPath = Path.Combine(_chromeCachePath, "playwright-browsers");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", playwrightPath);

        _logger.LogInformation("Thumbnail generator - Chrome cache: {ChromePath}", _chromeCachePath);
    }

    /// <summary>Creates a generator with a controllable capture seam for focused tests.</summary>
    internal TripMapThumbnailGenerator(
        ILogger<TripMapThumbnailGenerator> logger,
        IWebHostEnvironment env,
        IConfiguration configuration,
        Func<CancellationToken, Task<byte[]?>> captureOverride)
        : this(logger, env, configuration)
    {
        _captureOverride = captureOverride;
    }

    /// <summary>
    /// Ensures Playwright browsers are installed. Uses semaphore to prevent concurrent installations.
    /// </summary>
    private async Task EnsureBrowsersInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_browsersInstalled) return;

        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (_browsersInstalled) return;

            _logger.LogInformation("Checking Playwright browser installation for thumbnails...");

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

            if (exitCode != 0)
            {
                _logger.LogWarning("Playwright browser installation returned exit code {ExitCode}", exitCode);
            }
            else
            {
                _logger.LogInformation("Playwright browsers ready for thumbnails");
            }

            _browsersInstalled = true;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Gets or generates a thumbnail URL for a trip's map.
    /// Screenshots the public embed view to create self-hosted thumbnails using local tile cache.
    /// </summary>
    public async Task<string?> GetOrGenerateThumbnailAsync(
        Guid tripId,
        double centerLat,
        double centerLon,
        int zoom,
        int width,
        int height,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        // Validate coordinates
        if (centerLat < -90 || centerLat > 90 || centerLon < -180 || centerLon > 180)
        {
            _logger.LogWarning("Invalid coordinates for trip {TripId}: lat={Lat}, lon={Lon}",
                tripId, centerLat, centerLon);
            return null;
        }

        // Clamp zoom to reasonable range
        zoom = Math.Clamp(zoom, 1, 18);

        // Build filename: {tripId}-{width}x{height}-{ticks}.jpg
        var filename = $"{tripId}-{width}x{height}.jpg";
        var filePath = Path.Combine(_thumbsDirectory, filename);

        // Check if thumbnail exists and is fresh (newer than trip's UpdatedAt)
        if (File.Exists(filePath))
        {
            var fileTime = File.GetLastWriteTimeUtc(filePath);
            if (fileTime >= updatedAt)
            {
                // Cached version is fresh - add timestamp for browser cache busting
                var timestamp = updatedAt.Ticks;
                return $"/thumbs/trips/{filename}?v={timestamp}";
            }
        }

        // Generate new thumbnail by screenshotting the embed view
        try
        {
            byte[]? thumbnailBytes;
            if (_captureOverride != null)
            {
                thumbnailBytes = await _captureOverride(cancellationToken);
            }
            else
            {
                await EnsureBrowsersInstalledAsync(cancellationToken);
                thumbnailBytes = await CaptureEmbedViewAsync(
                    tripId, centerLat, centerLon, zoom, width, height, cancellationToken);
            }

            if (thumbnailBytes != null && thumbnailBytes.Length > 0)
            {
                // Save to disk
                await File.WriteAllBytesAsync(filePath, thumbnailBytes, cancellationToken);

                // Set file timestamp to match trip's UpdatedAt for cache validation
                File.SetLastWriteTimeUtc(filePath, updatedAt);

                _logger.LogInformation("Generated thumbnail for trip {TripId}: {Width}x{Height}, saved to: {FilePath}",
                    tripId, width, height, filePath);

                // Add timestamp for browser cache busting
                var timestamp = updatedAt.Ticks;
                return $"/thumbs/trips/{filename}?v={timestamp}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for trip {TripId}", tripId);
        }

        return null;
    }

    /// <summary>
    /// Gets the local base URL for Playwright to access the app on the same server.
    /// Uses http://127.0.0.1:{port} for secure loopback communication.
    /// </summary>
    private string GetLocalBaseUrl()
    {
        // Try to get Kestrel HTTP endpoint from configuration
        var httpUrl = _configuration["Kestrel:Endpoints:Http:Url"];

        if (!string.IsNullOrWhiteSpace(httpUrl))
        {
            // Parse the port from the URL (e.g., "http://localhost:5000" or "http://*:5000")
            if (Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri))
            {
                return $"http://127.0.0.1:{uri.Port}";
            }

            // Handle format like "http://*:5000" or "http://+:5000"
            var portMatch = System.Text.RegularExpressions.Regex.Match(httpUrl, @":(\d+)");
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var portNumber))
            {
                return $"http://127.0.0.1:{portNumber}";
            }
        }

        // Try ASPNETCORE_URLS environment variable (common in production)
        var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(aspnetcoreUrls))
        {
            // Split by semicolon, look for http:// URL
            var urls = aspnetcoreUrls.Split(';');
            foreach (var url in urls)
            {
                if (url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var envUri))
                    {
                        return $"http://127.0.0.1:{envUri.Port}";
                    }
                }
            }
        }

        // Fallback to common default port
        _logger.LogWarning("Could not determine Kestrel HTTP port from configuration, using default http://127.0.0.1:5000");
        return "http://127.0.0.1:5000";
    }

    /// <summary>
    /// Builds an authorized request authority whose DNS resolution is pinned to loopback.
    /// </summary>
    internal (string EmbedUrl, string HostResolverRule)? BuildCaptureSettings(
        Guid tripId,
        double lat,
        double lon,
        int zoom)
    {
        var authorizedHost = TileCacheService.GetFirstAuthorizedPublicHost(_configuration);
        if (authorizedHost == null)
        {
            return null;
        }

        var localUri = new Uri(GetLocalBaseUrl());
        var thumbnailZoom = Math.Max(1, zoom - 1);
        var embedUrl = $"http://{authorizedHost}:{localUri.Port}/Public/Trips/{tripId}" +
            $"?embed=true&lat={lat.ToString("F6", CultureInfo.InvariantCulture)}" +
            $"&lon={lon.ToString("F6", CultureInfo.InvariantCulture)}&zoom={thumbnailZoom}";
        return (embedUrl, $"MAP {authorizedHost} 127.0.0.1");
    }

    /// <summary>
    /// Captures a screenshot of the trip embed view using Playwright.
    /// </summary>
    private async Task<byte[]?> CaptureEmbedViewAsync(
        Guid tripId,
        double lat,
        double lon,
        int zoom,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var captureSettings = BuildCaptureSettings(tripId, lat, lon, zoom);
        if (captureSettings == null)
        {
            _logger.LogWarning(
                "Thumbnail capture skipped for trip {TripId}: no authorized public hostname is configured",
                tripId);
            return null;
        }

        _logger.LogDebug("Capturing thumbnail for trip {TripId} through the loopback endpoint", tripId);

        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var launchArgs = new List<string>
        {
            "--ignore-certificate-errors",
            "--disable-web-security",
            $"--host-resolver-rules={captureSettings.Value.HostResolverRule}"
        };

        // ARM64 Linux specific flags
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            launchArgs.AddRange(new[]
            {
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            });
        }

        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = launchArgs
        });
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height }
            });
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await CapturePageAsync(
                    page, captureSettings.Value.EmbedUrl, cancellationToken);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    /// <summary>Captures only after Playwright confirms a successful main-document response.</summary>
    internal static async Task<byte[]?> CapturePageAsync(
        IPage page,
        string embedUrl,
        CancellationToken cancellationToken)
    {
        var response = await page.GotoAsync(embedUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30000
        });
        cancellationToken.ThrowIfCancellationRequested();

        if (response?.Ok != true ||
            !string.Equals(response.Url, embedUrl, StringComparison.Ordinal))
        {
            return null;
        }

        return await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Jpeg,
            Quality = 85,
            FullPage = false
        });
    }

    /// <summary>
    /// Deletes all cached thumbnails for a specific trip.
    /// (No-op for external API approach; will be implemented for self-hosted tiles)
    /// </summary>
    public void DeleteThumbnails(Guid tripId)
    {
        // For external API approach, no cleanup needed
        // If/when we implement self-hosted thumbnails, we'll delete files here

        try
        {
            var pattern = $"{tripId}-*.jpg";
            var files = Directory.GetFiles(_thumbsDirectory, pattern);

            foreach (var file in files)
            {
                File.Delete(file);
                _logger.LogInformation("Deleted thumbnail: {File}", file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete thumbnails for trip {TripId}", tripId);
        }
    }

    /// <summary>
    /// Scans the thumbnail directory and removes orphaned thumbnails.
    /// </summary>
    public Task<int> CleanupOrphanedThumbnailsAsync(ISet<Guid> existingTripIds)
    {
        var deleted = 0;

        try
        {
            var files = Directory.GetFiles(_thumbsDirectory, "*.jpg");

            foreach (var file in files)
            {
                // Extract trip ID from filename: {tripId}-{width}x{height}-{ticks}.jpg
                var filename = Path.GetFileNameWithoutExtension(file);
                var parts = filename.Split('-');

                if (parts.Length > 0 && Guid.TryParse(parts[0], out var tripId))
                {
                    if (!existingTripIds.Contains(tripId))
                    {
                        File.Delete(file);
                        deleted++;
                        _logger.LogInformation("Deleted orphaned thumbnail: {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup orphaned thumbnails");
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Deletes all thumbnails for a trip to force regeneration.
    /// Called when a trip is updated to ensure thumbnails reflect the latest map state.
    /// </summary>
    public void InvalidateThumbnails(Guid tripId, DateTime updatedAt)
    {
        // Delete all thumbnails for this trip - they'll be regenerated on next request
        // with the updated map state and new timestamp
        try
        {
            var pattern = $"{tripId}-*.jpg";
            var files = Directory.GetFiles(_thumbsDirectory, pattern);

            foreach (var file in files)
            {
                File.Delete(file);
                _logger.LogInformation("Invalidated thumbnail for updated trip: {File}", file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate thumbnails for trip {TripId}", tripId);
        }
    }
}
