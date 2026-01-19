using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Areas.Public.Controllers;

// JS usage example 
// var baseUrl = window.location.origin;  // This will be "http://localhost:5000" in dev or "https://yourdomain.com" in prod
// var tileUrl = baseUrl + "/tiles/{z}/{x}/{y}.png";
// L.tileLayer(tileUrl, {
//     maxZoom: 19,
//     attribution: '&copy; OpenStreetMap contributors'
// }).addTo(map);
/// <summary>
/// Controller for serving cached map tiles via a proxy to upstream tile providers.
/// </summary>
[Area("Public")]
[Route("Public/tiles")]
public class TilesController : Controller
{
    /// <summary>
    /// Maximum supported zoom level for tile requests.
    /// Most tile providers support up to zoom 22, some go to 24.
    /// </summary>
    private const int MaxZoomLevel = 22;

    /// <summary>
    /// Maximum number of IPs to track for rate limiting to prevent memory exhaustion.
    /// </summary>
    private const int MaxTrackedIps = 100000;

    /// <summary>
    /// Thread-safe dictionary for rate limiting anonymous tile requests by IP address.
    /// Uses atomic operations to prevent race conditions between check and increment.
    /// </summary>
    private static readonly ConcurrentDictionary<string, RateLimitEntry> RateLimitCache = new();

    /// <summary>
    /// Tracks the request count and window expiration for rate limiting.
    /// </summary>
    private sealed class RateLimitEntry
    {
        private int _count;
        private long _expirationTicks;

        public RateLimitEntry(long expirationTicks)
        {
            _count = 0; // Starts at 0, IncrementAndGet will return 1 for first request
            _expirationTicks = expirationTicks;
        }

        /// <summary>
        /// Atomically increments the counter and returns the new count.
        /// If the window has expired, resets the counter to 1 and updates expiration.
        /// </summary>
        public int IncrementAndGet(long currentTicks, long newExpirationTicks)
        {
            // Check if window expired - reset if so
            if (currentTicks > Interlocked.Read(ref _expirationTicks))
            {
                Interlocked.Exchange(ref _expirationTicks, newExpirationTicks);
                Interlocked.Exchange(ref _count, 0);
            }

            return Interlocked.Increment(ref _count);
        }

        public bool IsExpired(long currentTicks) => currentTicks > Interlocked.Read(ref _expirationTicks);
    }

    private readonly ILogger<TilesController> _logger;
    private readonly TileCacheService _tileCacheService;
    private readonly IApplicationSettingsService _settingsService;

    public TilesController(ILogger<TilesController> logger, TileCacheService tileCacheService, IApplicationSettingsService settingsService)
    {
        _logger = logger;
        _tileCacheService = tileCacheService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Endpoint to serve cached tiles.
    /// Example URL: /tiles/10/512/384.png
    /// </summary>
    [HttpGet("{z:int}/{x:int}/{y:int}.png")]
    public async Task<IActionResult> GetTile(int z, int x, int y)
    {
        // Validate the referer header to prevent third-party exploitation.
        string? refererValue = Request.Headers["Referer"].ToString();
        if (string.IsNullOrEmpty(refererValue) || !IsValidReferer(refererValue))
        {
            _logger.LogWarning("Unauthorized tile request. Referer: {Referer}", refererValue ?? "null");
            return Unauthorized("Unauthorized request.");
        }

        // Validate tile coordinates are within acceptable bounds.
        if (z < 0 || z > MaxZoomLevel || x < 0 || y < 0)
        {
            _logger.LogWarning("Invalid tile coordinates requested: z={Z}, x={X}, y={Y}", z, x, y);
            return BadRequest("Invalid tile coordinates.");
        }

        // Resolve the tile provider template from settings or presets.
        var settings = _settingsService.GetSettings();

        // Rate limit anonymous requests to prevent tile scraping abuse.
        // Authenticated users (logged-in) are never rate limited.
        if (User.Identity?.IsAuthenticated != true && settings.TileRateLimitEnabled)
        {
            var clientIp = GetClientIpAddress();
            if (IsRateLimitExceeded(clientIp, settings.TileRateLimitPerMinute))
            {
                _logger.LogWarning("Tile rate limit exceeded for IP: {ClientIp}", clientIp);
                return StatusCode(429, "Too many requests. Please try again later.");
            }
        }
        var preset = TileProviderCatalog.FindPreset(settings.TileProviderKey);
        var template = preset?.UrlTemplate ?? settings.TileProviderUrlTemplate;
        var apiKey = TileProviderCatalog.RequiresApiKey(template) ? settings.TileProviderApiKey : null;

        if (!TileProviderCatalog.TryBuildTileUrl(template, apiKey, z, x, y, out var tileUrl, out var error))
        {
            _logger.LogError("Tile provider configuration error: {Error}", error);
            return StatusCode(500, "Tile provider misconfigured.");
        }

        // Call the tile cache service to retrieve the tile.
        // The service will either return the cached tile data or (if missing) download, cache, and then return it.
        var tileData = await _tileCacheService.RetrieveTileAsync(z.ToString(), x.ToString(), y.ToString(), tileUrl);
        if (tileData == null)
        {
            _logger.LogError("Tile data not found for {z}/{x}/{y}", z, x, y);
            return NotFound("Tile not found.");
        }

        // Return the tile data with the appropriate content type.
        return File(tileData, "image/png");
    }

    /// <summary>
    /// Validates that the request's Referer header originates from our own domain.
    /// This method dynamically infers the base URL from the current request.
    /// </summary>
    private bool IsValidReferer(string referer)
    {
        if (string.IsNullOrEmpty(referer))
            return false;

        try
        {
            var refererUri = new Uri(referer);
            var requestHost = Request.Host.Host;
            return refererUri.Host == requestHost;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the client IP address, respecting X-Forwarded-For header only when behind a trusted proxy.
    /// Only trusts the header if the direct connection is from localhost or private IP ranges,
    /// which indicates a reverse proxy is in use. This prevents spoofing attacks.
    /// </summary>
    private string GetClientIpAddress()
    {
        var directIp = HttpContext.Connection.RemoteIpAddress;
        var directIpString = directIp?.ToString() ?? "unknown";

        // Only trust X-Forwarded-For if the direct connection is from a trusted proxy
        // (localhost or private IP range indicates reverse proxy setup)
        if (directIp != null && IsTrustedProxyIp(directIp))
        {
            var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                // X-Forwarded-For can contain multiple IPs: "client, proxy1, proxy2"
                // The first IP is the original client
                var clientIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(clientIp))
                {
                    return clientIp;
                }
            }
        }

        // Fallback to direct connection IP
        return directIpString;
    }

    /// <summary>
    /// Determines if an IP address is from a trusted proxy (localhost or private range).
    /// </summary>
    private static bool IsTrustedProxyIp(System.Net.IPAddress ip)
    {
        // Loopback (127.0.0.1, ::1)
        if (System.Net.IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();

        // IPv4 private ranges
        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
        }

        // IPv6 link-local (fe80::/10) - common in Docker/container setups
        if (bytes.Length == 16 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return true;

        return false;
    }

    /// <summary>
    /// Checks if the given IP has exceeded the rate limit and atomically increments the counter.
    /// Uses a fixed window approach with 1-minute expiration.
    /// Thread-safe: uses atomic operations to prevent race conditions.
    /// </summary>
    /// <param name="clientIp">The client IP address to check.</param>
    /// <param name="maxRequestsPerMinute">Maximum allowed requests per minute.</param>
    /// <returns>True if rate limit is exceeded, false otherwise.</returns>
    private static bool IsRateLimitExceeded(string clientIp, int maxRequestsPerMinute)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var expirationTicks = currentTicks + TimeSpan.FromMinutes(1).Ticks;

        // Periodically clean up expired entries to prevent unbounded growth
        // Only clean up when cache gets large (amortized cost)
        if (RateLimitCache.Count > MaxTrackedIps)
        {
            CleanupExpiredEntries(currentTicks);
        }

        var entry = RateLimitCache.GetOrAdd(clientIp, _ => new RateLimitEntry(expirationTicks));
        var count = entry.IncrementAndGet(currentTicks, expirationTicks);

        return count > maxRequestsPerMinute;
    }

    /// <summary>
    /// Removes expired entries from the rate limit cache to prevent memory growth.
    /// Called periodically when cache exceeds size threshold.
    /// </summary>
    private static void CleanupExpiredEntries(long currentTicks)
    {
        foreach (var kvp in RateLimitCache)
        {
            if (kvp.Value.IsExpired(currentTicks))
            {
                RateLimitCache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
