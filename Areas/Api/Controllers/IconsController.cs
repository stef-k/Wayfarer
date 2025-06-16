using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using Wayfarer.Models;
using System.Text.RegularExpressions;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/icons")]
[ApiController]
public class IconsController : BaseApiController
{
    private readonly IWebHostEnvironment _env;

    public IconsController(ApplicationDbContext dbContext, ILogger<IconsController> logger, IWebHostEnvironment env)
        : base(dbContext, logger)
    {
        _env = env;
    }

    /// GET: /api/icons?layout=marker|circle
    [HttpGet]
    public IActionResult GetIcons([FromQuery] string layout = "marker")
    {
        var validLayouts = new[] { "marker", "circle" };
        layout = layout.ToLowerInvariant();

        if (!validLayouts.Contains(layout))
            return BadRequest("Layout must be 'marker' or 'circle'.");

        var iconDir = Path.Combine(_env.WebRootPath, "icons", "wayfarer-map-icons", "dist", layout);

        if (!Directory.Exists(iconDir))
            return NotFound($"Icon directory '{layout}' not found.");

        var icons = Directory.GetFiles(iconDir, "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name)
            .ToList();

        return Ok(icons);
    }

    /// GET: /api/icons/colors
    [HttpGet("colors")]
    public IActionResult GetAvailableColors()
    {
        var cssPath = Path.Combine(_env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css");

        if (!System.IO.File.Exists(cssPath))
            return NotFound("CSS file not found.");

        try
        {
            var cssContent = System.IO.File.ReadAllText(cssPath);

            // Match class selectors like ".bg-blue", ".color-purple", etc.
            var bgMatches = Regex.Matches(cssContent, @"\.bg-[\w-]+");
            var colorMatches = Regex.Matches(cssContent, @"\.color-[\w-]+");

            var bgList = bgMatches
                .Select(m => m.Value.TrimStart('.'))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var colorList = colorMatches
                .Select(m => m.Value.TrimStart('.'))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return Ok(new
            {
                backgrounds = bgList,
                glyphs = colorList
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse color classes.");
            return StatusCode(500, "Error reading CSS file.");
        }
    }
}