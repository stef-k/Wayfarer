using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Same-origin API surface for the private Vue Trip Editor workspace.
/// </summary>
[Area("Api")]
[ApiController]
[Route("api/trips/{tripId:guid}/editor")]
public sealed class TripEditorController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TripEditorController> _logger;

    /// <summary>
    /// Initializes a new instance of the Trip Editor API controller.
    /// </summary>
    public TripEditorController(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        ILogger<TripEditorController> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _logger = logger;
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

        if (User?.IsInRole("User") != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
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
            var tripExists = await _dbContext.Trips
                .AsNoTracking()
                .AnyAsync(t => t.Id == tripId, cancellationToken);

            return tripExists ? StatusCode(StatusCodes.Status403Forbidden) : NotFound();
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

        try
        {
            return Ok(EditorTripStateMapper.ToEditorState(
                trip,
                visitsByPlaceId,
                BuildOptions(),
                GeneratePublicTripUrl(trip.Id),
                GenerateProgressPublicTripUrl(trip.Id)));
        }
        catch (EditorInvalidAreaGeometryException ex)
        {
            _logger.LogError(ex, "Invalid area geometry while loading editor state for Trip {TripId}, Area {AreaId}.", ex.TripId, ex.AreaId);
            return InvalidAreaGeometryProblem(ex);
        }
    }

    private EditorOptionsDto BuildOptions() =>
        new(
            ReadIconNames(),
            ReadMarkerColorClasses().Backgrounds,
            ReadMarkerColorClasses().Glyphs,
            SegmentTransportModes.Options,
            new EditorAreaDefaultsDto("Area", "#ff6600"),
            new EditorTagOptionsDto(25, 8, "Letters, numbers, spaces, hyphens, and apostrophes."),
            new EditorLimitsDto(6, 1));

    private string? GeneratePublicTripUrl(Guid tripId) =>
        Url.Action("View", "TripViewer", new { area = "Public", id = tripId }, Request.Scheme);

    private string? GenerateProgressPublicTripUrl(Guid tripId) =>
        Url.Action("View", "TripViewer", new { area = "Public", id = tripId, progress = 1 }, Request.Scheme);

    private static ObjectResult InvalidAreaGeometryProblem(EditorInvalidAreaGeometryException exception)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Type = "https://wayfarer/errors/editor-invalid-area-geometry",
            Title = "Invalid persisted area geometry."
        };
        problem.Extensions["areaId"] = exception.AreaId;
        problem.Extensions["tripId"] = exception.TripId;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status500InternalServerError,
            ContentTypes = { "application/problem+json" }
        };
    }

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
        Regex.Matches(css, $@"\.{Regex.Escape(prefix.TrimStart('.'))}[\w-]+")
            .Select(match => match.Value.TrimStart('.'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
