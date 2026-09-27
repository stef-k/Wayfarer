using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>
/// Service for generating trip map thumbnails using Playwright screenshots.
/// Screenshots the public embed view of trips to create thumbnails that use the app's local tile cache.
/// </summary>
public sealed partial class TripMapThumbnailGenerator : ITripMapThumbnailGenerator
{
    private readonly ILogger<TripMapThumbnailGenerator> _logger;
    private readonly TripThumbnailStorage _storage;
    private readonly IConfiguration _configuration;
    private readonly string _thumbsDirectory;
    private readonly Func<CancellationToken, Task<byte[]?>>? _captureOverride;

    /// <summary>
    /// Initializes the thumbnail generator.
    /// </summary>
    public TripMapThumbnailGenerator(
        ILogger<TripMapThumbnailGenerator> logger,
        TripThumbnailStorage storage,
        IConfiguration configuration)
    {
        _logger = logger;
        _storage = storage;
        _configuration = configuration;

        // Prepare thumbs directory
        _thumbsDirectory = storage.Root;
        Directory.CreateDirectory(_thumbsDirectory);
        _logger.LogInformation("Thumbnail directory: {ThumbsDirectory}", _thumbsDirectory);

    }

    /// <summary>Creates a generator with a controllable capture seam for focused tests.</summary>
    internal TripMapThumbnailGenerator(
        ILogger<TripMapThumbnailGenerator> logger,
        TripThumbnailStorage storage,
        IConfiguration configuration,
        Func<CancellationToken, Task<byte[]?>> captureOverride)
        : this(logger, storage, configuration)
    {
        _captureOverride = captureOverride;
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
        BrowserCapturePolicy.ValidateThumbnail(width, height);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate coordinates
        if (centerLat < -90 || centerLat > 90 || centerLon < -180 || centerLon > 180)
        {
            _logger.LogWarning("Invalid coordinates for trip {TripId}: lat={Lat}, lon={Lon}",
                tripId, centerLat, centerLon);
            return null;
        }

        // Clamp zoom to reasonable range
        zoom = Math.Clamp(zoom, 1, 18);

        // Resolve the canonical generated JPEG under the current external root.
        var filename = TripThumbnailStorage.FileName(tripId, width, height);
        var filePath = _storage.Resolve(filename);

        // Check if thumbnail exists and is fresh (newer than trip's UpdatedAt)
        if (File.Exists(filePath))
        {
            var fileTime = File.GetLastWriteTimeUtc(filePath);
            if (fileTime >= updatedAt)
            {
                // Cached version is fresh - add timestamp for browser cache busting
                return _storage.PublicUrl(tripId, width, height, updatedAt);
            }
        }

        using var admission = BrowserAdmission.Shared.TryAcquire();
        if (admission == null) return null;

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
                thumbnailBytes = await CaptureEmbedViewAsync(
                    tripId, centerLat, centerLon, zoom, width, height, cancellationToken);
            }

            if (thumbnailBytes != null && thumbnailBytes.Length > 0)
            {
                await PersistThumbnailAsync(filePath, thumbnailBytes, updatedAt, cancellationToken);

                _logger.LogInformation("Generated thumbnail for trip {TripId}: {Width}x{Height}, saved to: {FilePath}",
                    tripId, width, height, filePath);

                // Add timestamp for browser cache busting
                return _storage.PublicUrl(tripId, width, height, updatedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for trip {TripId}", tripId);
        }

        return null;
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
        var policy = BrowserCapturePolicy.Resolve(_configuration);
        if (policy == null) return null;
        var thumbnailZoom = Math.Max(1, zoom - 1);
        var embedUrl = policy.Url($"/Public/Trips/{tripId}") +
            $"?embed=true&lat={lat.ToString("F6", CultureInfo.InvariantCulture)}" +
            $"&lon={lon.ToString("F6", CultureInfo.InvariantCulture)}&zoom={thumbnailZoom}";
        return (embedUrl, policy.HostResolverRule);
    }

    /// <summary>
    /// Captures a screenshot of the trip embed view using Playwright.
    /// </summary>
    internal async Task<byte[]?> CaptureEmbedViewAsync(
        Guid tripId,
        double lat,
        double lon,
        int zoom,
        int width,
        int height,
        CancellationToken cancellationToken,
        Func<Task<IPlaywright>>? playwrightFactory = null)
    {
        BrowserCapturePolicy.ValidateThumbnail(width, height);
        var policy = BrowserCapturePolicy.Resolve(_configuration);
        var settings = BuildCaptureSettings(tripId, lat, lon, zoom);
        if (policy == null || settings == null) return null;
        await using var workflow = await BrowserWorkflow.StartAsync(policy, cancellationToken, playwrightFactory);
        try
        {
            await using var context = await workflow.NewContextAsync(width, height);
            var page = await context.NewPageAsync();
            var bytes = await CapturePageAsync(page, settings.Value.EmbedUrl, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
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

        // Omit only the shared embed escape; retain screen media and the existing map framing.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = ".wayfarer-embed-full-view { display: none !important; }"
        });

        return await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Jpeg,
            Quality = 85,
            FullPage = false
        });
    }

    /// <summary>
    /// Deletes all cached thumbnails for a specific trip.
    /// </summary>
    public void DeleteThumbnails(Guid tripId)
    {
        try
        {
            var pattern = $"{tripId}-*.jpg";
            var files = Directory.GetFiles(_thumbsDirectory, pattern);

            foreach (var file in files)
            {
                if (!TripThumbnailStorage.TryParse(Path.GetFileName(file), out var parsedId) || parsedId != tripId) continue;
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
                if (TripThumbnailStorage.TryParse(Path.GetFileName(file), out var tripId))
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
                if (!TripThumbnailStorage.TryParse(Path.GetFileName(file), out var parsedId) || parsedId != tripId) continue;
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
