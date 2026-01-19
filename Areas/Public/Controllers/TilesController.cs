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
[Area("Public")]
[Route("Public/tiles")]
public class TilesController : Controller
{
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

        // Resolve the tile provider template from settings or presets.
        var settings = _settingsService.GetSettings();
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
}
