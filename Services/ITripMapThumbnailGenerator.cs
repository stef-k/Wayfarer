namespace Wayfarer.Services;

/// <summary>
/// Lightweight service for generating and managing trip map thumbnails.
/// Uses tile stitching instead of browser automation for better performance.
/// </summary>
public interface ITripMapThumbnailGenerator
{
    /// <summary>
    /// Gets or generates a thumbnail URL for a trip's map.
    /// Returns cached version if available and not stale (based on trip's UpdatedAt).
    /// </summary>
    /// <param name="tripId">Trip identifier</param>
    /// <param name="centerLat">Map center latitude</param>
    /// <param name="centerLon">Map center longitude</param>
    /// <param name="zoom">Map zoom level (typically 8-14)</param>
    /// <param name="width">Thumbnail width in pixels</param>
    /// <param name="height">Thumbnail height in pixels</param>
    /// <param name="updatedAt">Trip's last update timestamp for cache invalidation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>URL to the thumbnail image, or null if generation failed</returns>
    Task<string?> GetOrGenerateThumbnailAsync(
        Guid tripId,
        double centerLat,
        double centerLon,
        int zoom,
        int width,
        int height,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cached thumbnails for a specific trip.
    /// Call this when a trip is deleted.
    /// </summary>
    /// <param name="tripId">Trip identifier</param>
    void DeleteThumbnails(Guid tripId);

    /// <summary>
    /// Scans the thumbnail directory and removes orphaned thumbnails
    /// (thumbnails for trips that no longer exist in the database).
    /// Should be called periodically by a background task.
    /// </summary>
    /// <param name="existingTripIds">Set of all current trip IDs from database</param>
    /// <returns>Number of orphaned thumbnails deleted</returns>
    Task<int> CleanupOrphanedThumbnailsAsync(ISet<Guid> existingTripIds);

    /// <summary>
    /// Deletes stale thumbnails for a trip (thumbnails older than the trip's UpdatedAt).
    /// Call this when a trip is updated to force regeneration on next request.
    /// </summary>
    /// <param name="tripId">Trip identifier</param>
    /// <param name="updatedAt">Trip's current UpdatedAt timestamp</param>
    void InvalidateThumbnails(Guid tripId, DateTime updatedAt);
}
