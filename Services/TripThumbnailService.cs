using Microsoft.Extensions.Logging;

namespace Wayfarer.Services;

/// <summary>
/// Default implementation of ITripThumbnailService.
/// Generates and serves trip thumbnail images with fallback strategies.
/// </summary>
public sealed class TripThumbnailService : ITripThumbnailService
{
    private readonly ILogger<TripThumbnailService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly ITripMapThumbnailGenerator _generator;

    /// <summary>
    /// Initializes a new instance of TripThumbnailService.
    /// </summary>
    public TripThumbnailService(
        ILogger<TripThumbnailService> logger,
        IWebHostEnvironment env,
        ITripMapThumbnailGenerator generator)
    {
        _logger = logger;
        _env = env;
        _generator = generator;
    }

    /// <summary>
    /// Gets the thumbnail URL for a trip.
    /// Priority: 1) Map snapshot (from CenterLat/Lon/Zoom), 2) CoverImageUrl, 3) Placeholder SVG.
    /// </summary>
    public string? GetThumbUrl(Guid id, double? lat, double? lon, int? zoom, string? coverImageUrl, string size = "800x450")
    {
        // TODO: Future enhancement - generate map snapshot if lat/lon/zoom are available
        // For now, we use the simpler fallback strategy

        // Priority 1: Map snapshot (not yet implemented)
        // if (lat.HasValue && lon.HasValue && zoom.HasValue)
        // {
        //     var snapshotPath = $"/thumbs/trips/{id}-{size}.jpg";
        //     var fullPath = Path.Combine(_env.WebRootPath, snapshotPath.TrimStart('/'));
        //
        //     // Check if snapshot exists and is recent, otherwise generate it
        //     // Return snapshot URL
        // }

        // Priority 2: CoverImageUrl
        if (!string.IsNullOrWhiteSpace(coverImageUrl))
        {
            return coverImageUrl;
        }

        // Priority 3: Placeholder SVG
        // Return a data URI for an inline SVG placeholder
        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='800' height='450' viewBox='0 0 800 450'%3E%3Crect width='800' height='450' fill='%23e9ecef'/%3E%3Cg transform='translate(400 225)'%3E%3Cpath fill='%236c757d' d='M-20-40 L20-40 L0-60 Z M-20-40 L-20 0 L20 0 L20-40 Z M-15-35 L-15-5 L15-5 L15-35 Z'/%3E%3C/g%3E%3C/svg%3E";
    }

    /// <summary>
    /// Gets the thumbnail URL for a trip (async version with map generation).
    /// Priority: 1) Map snapshot (from CenterLat/Lon/Zoom), 2) CoverImageUrl, 3) Placeholder SVG.
    /// </summary>
    public async Task<string?> GetThumbUrlAsync(
        Guid id,
        double? lat,
        double? lon,
        int? zoom,
        string? coverImageUrl,
        DateTime updatedAt,
        string size = "800x450",
        CancellationToken cancellationToken = default)
    {
        // Parse size string (e.g., "800x450")
        var parts = size.Split('x');
        var width = parts.Length >= 1 && int.TryParse(parts[0], out var w) ? w : 800;
        var height = parts.Length >= 2 && int.TryParse(parts[1], out var h) ? h : 450;

        // Priority 1: Map snapshot (if coordinates are available)
        if (lat.HasValue && lon.HasValue && zoom.HasValue)
        {
            try
            {
                var mapUrl = await _generator.GetOrGenerateThumbnailAsync(
                    id, lat.Value, lon.Value, zoom.Value,
                    width, height, updatedAt, cancellationToken);

                if (!string.IsNullOrWhiteSpace(mapUrl))
                {
                    return mapUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate map thumbnail for trip {TripId}", id);
                // Fall through to next priority
            }
        }

        // Priority 2: CoverImageUrl
        if (!string.IsNullOrWhiteSpace(coverImageUrl))
        {
            return coverImageUrl;
        }

        // Priority 3: Placeholder SVG
        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='800' height='450' viewBox='0 0 800 450'%3E%3Crect width='800' height='450' fill='%23e9ecef'/%3E%3Cg transform='translate(400 225)'%3E%3Cpath fill='%236c757d' d='M-20-40 L20-40 L0-60 Z M-20-40 L-20 0 L20 0 L20-40 Z M-15-35 L-15-5 L15-5 L15-35 Z'/%3E%3C/g%3E%3C/svg%3E";
    }
}
