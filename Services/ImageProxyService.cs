using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Contract for fetching, optimizing, and caching external images.
/// Used by the cache warm-up job and any component that needs to pre-populate
/// the proxied image cache without going through the HTTP controller pipeline.
/// </summary>
public interface IImageProxyService
{
    /// <summary>
    /// Fetches an external image, optimizes it via ImageSharp, and stores it in the
    /// proxied image cache. Returns true if the image was fetched and cached, false
    /// if already cached, disallowed, or failed.
    /// </summary>
    /// <param name="imageUrl">The external image URL to fetch and cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if newly cached, false otherwise.</returns>
    Task<bool> FetchAndCacheAsync(string imageUrl, CancellationToken ct = default);
}

/// <summary>
/// Fetches external images, optimizes them via ImageSharp, and stores them in the
/// proxied image disk cache. Delegates to <see cref="ImageProxyHelper"/> for
/// shared SSRF checks, cache key computation, and image optimization.
/// </summary>
public class ImageProxyService : IImageProxyService
{
    /// <summary>
    /// Maximum response body size in bytes for proxied images (20 MB).
    /// </summary>
    private const long MaxProxyImageBytes = 20 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IProxiedImageCacheService _imageCacheService;
    private readonly ILogger<ImageProxyService> _logger;

    public ImageProxyService(
        HttpClient httpClient,
        IProxiedImageCacheService imageCacheService,
        ILogger<ImageProxyService> logger)
    {
        _httpClient = httpClient;
        _imageCacheService = imageCacheService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> FetchAndCacheAsync(string imageUrl, CancellationToken ct = default)
    {
        // SSRF protection — shared with TripViewerController
        if (!ImageProxyHelper.IsUrlAllowed(imageUrl))
        {
            _logger.LogDebug("Image URL disallowed by SSRF check: {Url}", imageUrl);
            return false;
        }

        // Compute cache key with default proxy params (no resize, optimize=true)
        var cacheKey = ImageProxyHelper.ComputeImageCacheKey(imageUrl, null, null, null, true);

        // Already cached — skip
        var existing = await _imageCacheService.GetAsync(cacheKey);
        if (existing.HasValue)
            return false;

        // Download from origin
        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download image from {Url}.", imageUrl);
            return false;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Upstream returned {StatusCode} for image {Url}.", (int)resp.StatusCode, imageUrl);
                return false;
            }

            // Reject early if Content-Length exceeds limit
            if (resp.Content.Headers.ContentLength > MaxProxyImageBytes)
            {
                _logger.LogWarning("Image too large ({Size} bytes) from {Url}.", resp.Content.Headers.ContentLength, imageUrl);
                return false;
            }

            // Stream-read with hard cap
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            byte[] bytes;
            await using (var bodyStream = await resp.Content.ReadAsStreamAsync(ct))
            {
                using var limitedStream = new MemoryStream();
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await bodyStream.ReadAsync(buffer, ct)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxProxyImageBytes)
                    {
                        _logger.LogWarning("Image exceeded size limit during download from {Url}.", imageUrl);
                        return false;
                    }
                    limitedStream.Write(buffer, 0, read);
                }
                bytes = limitedStream.ToArray();
            }

            // Optimize via shared ImageSharp pipeline (same as TripViewerController)
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bytes = ImageProxyHelper.OptimizeImage(bytes, null, null, 95, out bool isPng);
                    contentType = isPng ? "image/png" : "image/jpeg";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to optimize image from {Url}, caching original.", imageUrl);
                }
            }

            // Store in cache
            await _imageCacheService.SetAsync(cacheKey, bytes, contentType);
            _logger.LogDebug("Warm-up cached image: {Url} ({Size} bytes).", imageUrl, bytes.Length);
            return true;
        }
    }
}
