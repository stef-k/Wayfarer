namespace Wayfarer.Services;

/// <summary>
/// Defines disk-cache operations for proxied and optimized external images.
/// </summary>
public interface IProxiedImageCacheService
{
    /// <summary>
    /// Returns an explicit cache result for the requested key.
    /// Fresh and stale hits include local bytes; misses and disk failures do not.
    /// </summary>
    Task<ProxiedImageCacheResult> GetAsync(string cacheKey);

    /// <summary>
    /// Stores processed image bytes under the given cache key.
    /// Existing entries are atomically replaced so readers see complete old or new bytes.
    /// </summary>
    Task SetAsync(string cacheKey, byte[] bytes, string contentType);

    /// <summary>
    /// Ensures the cache directory exists and initializes cache size tracking from the database.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Returns the total cached image size in megabytes.
    /// </summary>
    Task<double> GetCacheSizeInMbAsync();

    /// <summary>
    /// Returns the total number of cached images.
    /// </summary>
    Task<int> GetCachedImageCountAsync();
}

/// <summary>
/// Describes the cache state for a proxied image lookup.
/// </summary>
public enum ProxiedImageCacheStatus
{
    Miss,
    FreshHit,
    StaleHit,
    DiskMissingOrError
}

/// <summary>
/// Explicit result returned by proxied image cache reads.
/// </summary>
public sealed record ProxiedImageCacheResult(
    ProxiedImageCacheStatus Status,
    byte[]? Bytes,
    string? ContentType,
    string? FilePath)
{
    /// <summary>
    /// Gets whether this result contains bytes that can be returned to the browser.
    /// </summary>
    public bool HasBytes => Bytes is { Length: > 0 };
}
