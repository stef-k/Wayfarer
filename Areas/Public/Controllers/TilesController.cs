using Microsoft.AspNetCore.Mvc;

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

    public TilesController(ILogger<TilesController> logger, TileCacheService tileCacheService)
    {
        _logger = logger;
        _tileCacheService = tileCacheService;
    }

    /// <summary>
    /// Endpoint to serve cached tiles.
    /// Example URL: /tiles/10/512/384.png
    /// </summary>
    [HttpGet("{z:int}/{x:int}/{y:int}.png")]
    public async Task<IActionResult> GetTile(int z, int x, int y)
    {
        // Validate the referer header to prevent third-party exploitation.
        if (!Request.Headers.TryGetValue("Referer", out var referer) || !IsValidReferer(referer))
        {
            _logger.LogWarning("Unauthorized tile request. Referer: {Referer}", referer);
            return Unauthorized("Unauthorized request.");
        }
        
        // Construct the OSM tile URL. (Customize subdomain logic if desired.)
        string tileUrl = $"https://a.tile.openstreetmap.org/{z}/{x}/{y}.png";

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
        {
            return false;
        }

        // Dynamically build the base URL from the current request's scheme and host.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return referer.StartsWith(baseUrl);
    }

}