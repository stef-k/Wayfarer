namespace Wayfarer.Models;

/// <summary>
/// Lightweight metadata stored as a JSON sidecar file for tiles at zoom 0-8
/// that are not tracked in the database. Enables conditional requests and
/// cache header compliance for permanent (non-LRU) tiles.
/// Stored alongside tile files as {z}_{x}_{y}.png.meta.
/// </summary>
public class TileSidecarMetadata
{
    /// <summary>
    /// ETag value from the upstream tile server's response.
    /// Sent as If-None-Match on re-validation requests after expiry.
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Last-Modified header value from the upstream tile server's response.
    /// Sent as If-Modified-Since on re-validation requests after expiry.
    /// </summary>
    public DateTime? LastModifiedUpstream { get; set; }

    /// <summary>
    /// When the cached tile expires based on upstream Cache-Control/Expires headers.
    /// Before this time, the tile is served directly without re-validation.
    /// After this time, a conditional request is sent to check freshness.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
