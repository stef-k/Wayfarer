using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;

namespace Wayfarer.Areas.Api.Controllers;

[ApiController]
[Area("Api")]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = "Bearer")]
public class MbtileController : BaseApiController
{
    private readonly MbtileCacheService _mbtileService;

    public MbtileController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        MbtileCacheService mbtileService)
        : base(dbContext, logger)
    {
        _mbtileService = mbtileService;
    }

    /// <summary>
    /// Returns the manifest of currently cached tiles (tiles.json).
    /// </summary>
    [HttpGet]
    public IActionResult GetAvailableTiles()
    {
        try
        {
            var manifestPath = Path.Combine(_mbtileService.GetCacheDirectoryPath(), "tiles.json");
            if (!System.IO.File.Exists(manifestPath))
            {
                _logger.LogWarning("Tile manifest not found at {Path}", manifestPath);
                return NotFound(new { error = "Tile manifest not found." });
            }

            var json = System.IO.File.ReadAllText(manifestPath);
            var doc = JsonDocument.Parse(json);
            return Ok(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load tile manifest.");
            return StatusCode(500, new { error = "Server error loading tile data." });
        }
    }

    /// <summary>
    /// Downloads the raw MBTiles file for the specified region.
    /// </summary>
    [HttpGet("{region}.mbtiles")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public IActionResult DownloadMbtilesFile(string region)
    {
        var filePath = Path.Combine(_mbtileService.GetCacheDirectory(), $"{region}.mbtiles");
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "Tile file not found." });
        }

        return PhysicalFile(filePath, "application/octet-stream", Path.GetFileName(filePath));
    }

    /// <summary>
    /// Triggers download and cache of a missing MBTile from known upstreams (e.g., OpenMapTiles), and builds its routing.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<IActionResult> FetchRegion([FromQuery] string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return BadRequest(new { error = "Region is required." });
        }

        var user = GetUserFromToken();
        if (user == null)
        {
            return Unauthorized(new { error = "Invalid API token." });
        }

        bool result = await _mbtileService.DownloadAndCacheRemoteTileAsync(region);
        if (!result)
        {
            return StatusCode(500, new { error = $"Failed to fetch tile for region '{region}'." });
        }

        return Ok(new { message = $"Region '{region}' downloaded and cached successfully." });
    }
}