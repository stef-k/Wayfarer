namespace Wayfarer.Services;

/// <summary>
/// Contract for fetching, optimizing, refreshing, and caching external images.
/// </summary>
public interface IImageProxyService
{
    /// <summary>
    /// Gets a proxied image from cache or, when allowed, from the origin.
    /// </summary>
    Task<ImageProxyResult> GetOrFetchAsync(ImageProxyRequest request, bool allowOriginFetch, CancellationToken ct = default);

    /// <summary>
    /// Forces origin refresh for a cache key through the shared coordinator and process budget.
    /// </summary>
    Task<ImageProxyResult> RefreshAsync(ImageProxyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetches an external image and stores it in the proxied image cache.
    /// </summary>
    Task<bool> FetchAndCacheAsync(string imageUrl, CancellationToken ct = default);
}

/// <summary>
/// Immutable request parameters that determine a proxied image cache key and output bytes.
/// </summary>
public sealed record ImageProxyRequest(
    string Url,
    int? MaxWidth = null,
    int? MaxHeight = null,
    int? Quality = null,
    bool Optimize = true);

/// <summary>
/// Result status for proxied image service work.
/// </summary>
public enum ImageProxyResultStatus
{
    FreshHit,
    StaleHit,
    Fetched,
    OriginRequired,
    BadRequest,
    NotFound,
    TooLarge,
    Failed
}

/// <summary>
/// Result returned by the proxied image pipeline.
/// </summary>
public sealed record ImageProxyResult(
    ImageProxyResultStatus Status,
    string CacheKey,
    byte[]? Bytes,
    string? ContentType)
{
    /// <summary>
    /// Gets whether bytes are available to serve to the HTTP caller.
    /// </summary>
    public bool HasBytes => Bytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(ContentType);
}
