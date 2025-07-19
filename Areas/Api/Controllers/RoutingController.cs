using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Api.Controllers;

[ApiController]
[Area("Api")]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = "Bearer")]
public class RoutingController : BaseApiController
{
    private readonly RoutingCacheService _routingService;

    public RoutingController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger, RoutingCacheService routingService)
        : base(dbContext, logger)
    {
        _routingService = routingService;
    }

    /// <summary>
    /// Returns the list of cached routing files available on the server.
    /// </summary>
    [HttpGet]
    public IActionResult GetAvailableRoutings()
    {
        try
        {
            var dir = _routingService.GetCacheDirectoryPath();
            var list = Directory.GetFiles(dir, "*.routing")
                .Select(path => new
                {
                    region = Path.GetFileNameWithoutExtension(path),
                    sizeMB = Math.Round(new FileInfo(path).Length / 1024.0 / 1024.0, 2),
                    url = $"/api/routing/{Path.GetFileName(path)}"
                });

            return Ok(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load routing file list.");
            return StatusCode(500, new { error = "Server error loading routing data." });
        }
    }

    /// <summary>
    /// Downloads the .routing file for the specified region.
    /// </summary>
    [HttpGet("{region}.routing")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public IActionResult DownloadRouting(string region)
    {
        var path = _routingService.GetRoutingFilePath(region);
        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { error = "Routing file not found." });
        }

        return PhysicalFile(path, "application/octet-stream", Path.GetFileName(path));
    }
}