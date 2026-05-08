using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Same-origin API surface for the private Vue Trip Editor workspace.
/// </summary>
[Area("Api")]
[ApiController]
[Authorize(Roles = "User")]
[Route("api/trips/{tripId:guid}/editor")]
public sealed class TripEditorController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    /// <summary>
    /// Initializes a new instance of the Trip Editor API controller.
    /// </summary>
    public TripEditorController(ApplicationDbContext dbContext, IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _environment = environment;
    }

    /// <summary>
    /// Returns the read-only normalized editor state for an owned trip.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetEditorState(Guid tripId, CancellationToken cancellationToken)
    {
        var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            return NotFound();
        }

        var placeIds = trip.Regions
            .SelectMany(r => r.Places)
            .Select(p => p.Id)
            .ToArray();

        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);

        var visitsByPlaceId = visits
            .GroupBy(v => v.PlaceId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());

        return Ok(EditorTripStateMapper.ToEditorState(trip, visitsByPlaceId, BuildOptions()));
    }

    private EditorOptionsDto BuildOptions() =>
        new(
            ReadIconNames(),
            ReadMarkerColorClasses().Backgrounds,
            ReadMarkerColorClasses().Glyphs,
            new[]
            {
                new EditorTransportModeDto("walk", "Walk", 5),
                new EditorTransportModeDto("bike", "Bike", 15),
                new EditorTransportModeDto("drive", "Drive", 60),
                new EditorTransportModeDto("transit", "Transit", 35)
            },
            new EditorAreaDefaultsDto("Area", "#ff6600"),
            new EditorTagOptionsDto(25, 10, "Letters, numbers, spaces, hyphens, and apostrophes."),
            new EditorLimitsDto(6, 2));

    private IReadOnlyList<string> ReadIconNames()
    {
        var iconDir = Path.Combine(_environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        return Directory.Exists(iconDir)
            ? Directory.GetFiles(iconDir, "*.svg").Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Cast<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();
    }

    private (IReadOnlyList<string> Backgrounds, IReadOnlyList<string> Glyphs) ReadMarkerColorClasses()
    {
        var cssPath = Path.Combine(_environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css");
        if (!System.IO.File.Exists(cssPath))
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var css = System.IO.File.ReadAllText(cssPath);
        return (ReadClasses(css, ".bg-"), ReadClasses(css, ".color-"));
    }

    private static IReadOnlyList<string> ReadClasses(string css, string prefix) =>
        css.Split('{', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.StartsWith(prefix, StringComparison.Ordinal))
            .Select(part => part.Split(',', ' ', '\r', '\n', '\t').First().TrimStart('.'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name)
            .ToList();
}
