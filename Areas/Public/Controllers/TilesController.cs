using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;
using Wayfarer.Services;
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
    /// Thread-safe dictionary for rate limiting anonymous tile requests by IP address.
    /// Uses atomic operations via <see cref="RateLimitHelper"/> to prevent race conditions.
    /// </summary>
    private static readonly ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry> RateLimitCache = new();

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
        // First check zoom level to safely calculate max tile index.
        if (z < 0 || z > MaxZoomLevel)
        {
            _logger.LogWarning("Invalid tile coordinates requested: z={Z}, x={X}, y={Y}", z, x, y);
            return BadRequest("Invalid tile coordinates.");
        }

        // At zoom level z, valid tile coordinates are 0 to 2^z - 1.
        var maxTileIndex = (1 << z) - 1; // 2^z - 1
        if (x < 0 || y < 0 || x > maxTileIndex || y > maxTileIndex)
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
            if (RateLimitHelper.IsRateLimitExceeded(RateLimitCache, clientIp, settings.TileRateLimitPerMinute))
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

        // Set browser cache headers. Tiles are stable and rarely change;
        // 1-day browser caching eliminates redundant requests.
        Response.Headers["Cache-Control"] = "public, max-age=86400";

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
    /// Gets the client IP address using the shared <see cref="RateLimitHelper.GetClientIpAddress"/> utility.
    /// </summary>
    private string GetClientIpAddress() => RateLimitHelper.GetClientIpAddress(HttpContext);
}
