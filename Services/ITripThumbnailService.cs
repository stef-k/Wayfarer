namespace Wayfarer.Services;

/// <summary>
/// Service for generating and retrieving trip thumbnail images.
/// Supports map snapshots, cover images, and placeholder fallbacks.
/// </summary>
public interface ITripThumbnailService
{
    /// <summary>
    /// Gets the thumbnail URL for a trip (synchronous version).
    /// Priority: 1) CoverImageUrl, 2) Placeholder SVG.
    /// For map snapshots, use GetThumbUrlAsync instead.
    /// </summary>
    /// <param name="id">Trip identifier.</param>
    /// <param name="lat">Center latitude for map snapshot.</param>
    /// <param name="lon">Center longitude for map snapshot.</param>
    /// <param name="zoom">Zoom level for map snapshot.</param>
    /// <param name="coverImageUrl">Optional cover image URL to use as fallback.</param>
    /// <param name="size">Thumbnail size: "320x180" (list) or "800x450" (grid). Default is "800x450".</param>
    /// <returns>URL to the thumbnail image or placeholder.</returns>
    string? GetThumbUrl(Guid id, double? lat, double? lon, int? zoom, string? coverImageUrl, string size = "800x450");

    /// <summary>
    /// Gets the thumbnail URL for a trip (async version).
    /// Priority: 1) Map snapshot (from CenterLat/Lon/Zoom), 2) CoverImageUrl, 3) Placeholder SVG.
    /// </summary>
    /// <param name="id">Trip identifier.</param>
    /// <param name="lat">Center latitude for map snapshot.</param>
    /// <param name="lon">Center longitude for map snapshot.</param>
    /// <param name="zoom">Zoom level for map snapshot.</param>
    /// <param name="coverImageUrl">Optional cover image URL to use as fallback.</param>
    /// <param name="updatedAt">Trip's last update timestamp for cache invalidation.</param>
    /// <param name="size">Thumbnail size: "320x180" (list) or "800x450" (grid). Default is "800x450".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>URL to the thumbnail image or placeholder.</returns>
    Task<string?> GetThumbUrlAsync(Guid id, double? lat, double? lon, int? zoom, string? coverImageUrl, DateTime updatedAt, string size = "800x450", CancellationToken cancellationToken = default);
}
