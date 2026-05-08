using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/icons")]
[ApiController]
public class IconsController : BaseApiController
{
    private readonly IWebHostEnvironment _env;
    private readonly IIconColorProvider _iconColorProvider;

    private record IconPreview(string Icon, string Color, string Url);

    public IconsController(ApplicationDbContext dbContext, ILogger<IconsController> logger, IWebHostEnvironment env, IIconColorProvider iconColorProvider)
        : base(dbContext, logger)
    {
        _env = env;
        _iconColorProvider = iconColorProvider;
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
        try
        {
            var colors = _iconColorProvider.GetAvailableColors();
            if (colors == null)
                return NotFound("CSS file not found.");

            return Ok(new
            {
                backgrounds = colors.Backgrounds,
                glyphs = colors.Glyphs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse color classes.");
            return StatusCode(500, "Error reading CSS file.");
        }
    }


    /// GET: /api/icons/with-previews?layout=marker|circle
    [HttpGet("with-previews")]
    public IActionResult GetIconsWithPreviews([FromQuery] string layout = "marker")
    {
        var validLayouts = new[] { "marker", "circle" };
        layout = layout.ToLowerInvariant();

        if (!validLayouts.Contains(layout))
            return BadRequest("Layout must be 'marker' or 'circle'.");

        var basePath = Path.Combine(_env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "png", layout);
        var baseUrl = $"/icons/wayfarer-map-icons/dist/png/{layout}";

        if (!Directory.Exists(basePath))
            return NotFound("PNG icon directory not found.");

        var results = new List<IconPreview>();

        foreach (var colorDir in Directory.GetDirectories(basePath))
        {
            var color = Path.GetFileName(colorDir);
            foreach (var file in Directory.GetFiles(colorDir, "*.png"))
            {
                var icon = Path.GetFileNameWithoutExtension(file);
                var url = $"{baseUrl}/{color}/{icon}.png";

                results.Add(new IconPreview(icon, color, url));
            }
        }

        return Ok(results.OrderBy(x => x.Icon).ThenBy(x => x.Color));
    }
}
