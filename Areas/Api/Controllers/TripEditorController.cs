using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Util;

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
    private readonly IIconColorProvider _iconColorProvider;
    private readonly ITripMapThumbnailGenerator _thumbnailGenerator;
    private readonly ICacheWarmupScheduler _warmupScheduler;
    private readonly ILogger<TripEditorController> _logger;

    /// <summary>
    /// Initializes a new instance of the Trip Editor API controller.
    /// </summary>
    public TripEditorController(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IIconColorProvider iconColorProvider,
        ITripMapThumbnailGenerator thumbnailGenerator,
        ICacheWarmupScheduler warmupScheduler,
        ILogger<TripEditorController> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _iconColorProvider = iconColorProvider;
        _thumbnailGenerator = thumbnailGenerator;
        _warmupScheduler = warmupScheduler;
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

        var publicUrl = trip.IsPublic ? GeneratePublicTripUrl(trip.Id) : null;
        var progressPublicUrl = trip.IsPublic && trip.ShareProgressEnabled
            ? GenerateProgressPublicTripUrl(trip.Id)
            : null;

        try
        {
            return Ok(EditorTripStateMapper.ToEditorState(
                trip,
                visitsByPlaceId,
                BuildOptions(),
                publicUrl,
                progressPublicUrl));
        }
        catch (EditorInvalidAreaGeometryException ex)
        {
            _logger.LogError(ex, "Invalid area geometry while loading editor state for Trip {TripId}, Area {AreaId}.", ex.TripId, ex.AreaId);
            return InvalidAreaGeometryProblem(ex);
        }
    }

    /// <summary>
    /// Persists a complete metadata draft for an owned trip and returns the changed metadata slice.
    /// </summary>
    [HttpPatch("metadata")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (User?.IsInRole("User") != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Ownership is checked before request parsing so missing or non-owned trips stay hidden.
        var trip = await _dbContext.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        JsonElement request;
        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            request = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ValidationError(new Dictionary<string, string[]>
            {
                ["request"] = new[] { "Metadata update request must be valid JSON." }
            });
        }

        if (!EditorTripMetadataUpdateRequestParser.TryParse(request, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        var beforeExternalImages = BuildTripExternalImageSet(trip.CoverImageUrl, trip.Notes);
        var disableShareProgress = !update.IsPublic && trip.ShareProgressEnabled;
        var coverImageUrl = NormalizeOptionalUrl(update.CoverImage?.RawUrl);
        var updatedAt = DateTime.UtcNow;

        trip.Name = update.Name.Trim();
        trip.IsPublic = update.IsPublic;
        if (!update.IsPublic)
        {
            trip.ShareProgressEnabled = false;
        }

        trip.Notes = update.NotesHtml ?? string.Empty;
        trip.CoverImageUrl = coverImageUrl;
        trip.CenterLat = update.Center?.Latitude;
        trip.CenterLon = update.Center?.Longitude;
        trip.Zoom = update.Zoom;
        trip.UpdatedAt = updatedAt;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _thumbnailGenerator.InvalidateThumbnails(tripId, updatedAt);

        var afterExternalImages = BuildTripExternalImageSet(trip.CoverImageUrl, trip.Notes);
        var imagesNewlyIntroduced = afterExternalImages.Any(url => !beforeExternalImages.Contains(url));
        await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: imagesNewlyIntroduced);

        var metadata = EditorTripStateMapper.ToMetadata(
            trip,
            trip.IsPublic ? GeneratePublicTripUrl(trip.Id) : null,
            trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(trip.Id) : null);

        var warnings = disableShareProgress
            ? new[]
            {
                new EditorWarningDto(
                    "share-progress-disabled",
                    "Share progress was disabled because the trip is private.",
                    "trip",
                    trip.Id.ToString())
            }
            : Array.Empty<EditorWarningDto>();

        return Ok(new EditorMutationResult<EditorTripMetadataDto>(
            true,
            metadata,
            EditorAffectedSlicesDto.MetadataOnly(metadata),
            EditorDeletedIdsDto.Empty,
            warnings));
    }

    /// <summary>
    /// Creates a normal region for an owned trip.
    /// </summary>
    [HttpPost("regions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRegion(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        var request = await ParseJsonBody(cancellationToken);
        if (request == null)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Region save request must be valid JSON." } });
        }

        if (!EditorRegionRequestParser.TryParseSave(request.Value, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        var normalRegions = trip.Regions.Where(r => !IsShadowRegion(r)).ToList();
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            Trip = trip,
            UserId = userId!,
            Name = update.Name.Trim(),
            Notes = update.NotesHtml ?? string.Empty,
            CoverImageUrl = NormalizeOptionalUrl(update.CoverImage?.RawUrl),
            Center = ToPoint(update.Center),
            DisplayOrder = normalRegions.Count == 0 ? 1 : normalRegions.Max(r => r.DisplayOrder) + 1
        };

        _dbContext.Regions.Add(region);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var state = await LoadEditorStateForOwnedTrip(tripId, userId!, cancellationToken);
        var dto = state.RegionsById[region.Id];
        var affected = new EditorAffectedSlicesDto(
            null,
            new[] { dto },
            state.RegionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>> { [region.Id] = Array.Empty<Guid>() },
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>> { [region.Id] = Array.Empty<Guid>() },
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);

        return Ok(new EditorMutationResult<EditorRegionDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Updates a normal region for an owned trip.
    /// </summary>
    [HttpPut("regions/{regionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRegion(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return NotFound();
        }

        if (IsShadowRegion(region))
        {
            return ForbiddenProblem("The shadow region cannot be updated.");
        }

        var request = await ParseJsonBody(cancellationToken);
        if (request == null)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Region save request must be valid JSON." } });
        }

        if (!EditorRegionRequestParser.TryParseSave(request.Value, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        region.Name = update.Name.Trim();
        region.Notes = update.NotesHtml ?? string.Empty;
        region.CoverImageUrl = NormalizeOptionalUrl(update.CoverImage?.RawUrl);
        region.Center = ToPoint(update.Center);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = EditorTripStateMapper.ToRegion(region);
        var affected = new EditorAffectedSlicesDto(
            null,
            new[] { dto },
            null,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);

        return Ok(new EditorMutationResult<EditorRegionDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Deletes a normal region and returns authoritative deleted IDs and affected slices.
    /// </summary>
    [HttpDelete("regions/{regionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRegion(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return NotFound();
        }

        if (IsShadowRegion(region))
        {
            return ForbiddenProblem("The shadow region cannot be deleted.");
        }

        var deletedPlaceIds = region.Places.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Id).ToList();
        var deletedAreaIds = region.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Id).Select(a => a.Id).ToList();
        var deletedSegmentIds = trip.Segments
            .Where(s => (s.FromPlaceId.HasValue && deletedPlaceIds.Contains(s.FromPlaceId.Value))
                || (s.ToPlaceId.HasValue && deletedPlaceIds.Contains(s.ToPlaceId.Value)))
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToList();

        var deletedSegments = trip.Segments.Where(s => deletedSegmentIds.Contains(s.Id)).ToList();
        _dbContext.Segments.RemoveRange(deletedSegments);
        _dbContext.Areas.RemoveRange(region.Areas);
        _dbContext.Places.RemoveRange(region.Places);
        _dbContext.Regions.Remove(region);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await NormalizeRegionOrders(tripId, userId!, cancellationToken);
        await NormalizeSegmentOrders(tripId, userId!, cancellationToken);

        var state = await LoadEditorStateForOwnedTrip(tripId, userId!, cancellationToken);
        var affected = new EditorAffectedSlicesDto(
            null,
            Array.Empty<EditorRegionDto>(),
            state.RegionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            state.SegmentOrder,
            Array.Empty<EditorTagDto>(),
            null,
            state.VisitProgress,
            null);
        var deletedIds = new EditorDeletedIdsDto(
            new[] { regionId },
            deletedPlaceIds,
            deletedAreaIds,
            deletedSegmentIds,
            Array.Empty<string>());

        return Ok(new EditorMutationResult<EditorRegionDto?>(true, null, affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Persists the complete desired order for normal regions.
    /// </summary>
    [HttpPut("regions/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OrderRegions(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        var request = await ParseJsonBody(cancellationToken);
        if (request == null)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Region order request must be valid JSON." } });
        }

        if (!EditorRegionRequestParser.TryParseOrder(request.Value, out var orderRequest, out var errors))
        {
            return ValidationError(errors);
        }

        var shadow = trip.Regions.FirstOrDefault(IsShadowRegion);
        if (shadow != null && orderRequest.RegionIds.Contains(shadow.Id))
        {
            return ForbiddenProblem("The shadow region cannot be reordered.");
        }

        var normalRegions = trip.Regions.Where(r => !IsShadowRegion(r)).OrderBy(r => r.DisplayOrder).ThenBy(r => r.Name).ToList();
        var normalIds = normalRegions.Select(r => r.Id).ToList();
        if (orderRequest.RegionIds.Count != normalIds.Count
            || orderRequest.RegionIds.Distinct().Count() != orderRequest.RegionIds.Count
            || orderRequest.RegionIds.Any(id => !normalIds.Contains(id)))
        {
            return ValidationError(new Dictionary<string, string[]>
            {
                ["regionIds"] = new[] { "Region IDs must include every normal region in this trip exactly once." }
            });
        }

        if (shadow != null)
        {
            shadow.DisplayOrder = 0;
        }

        var byId = normalRegions.ToDictionary(r => r.Id);
        for (var i = 0; i < orderRequest.RegionIds.Count; i++)
        {
            byId[orderRequest.RegionIds[i]].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var state = await LoadEditorStateForOwnedTrip(tripId, userId!, cancellationToken);
        var updatedRegions = orderRequest.RegionIds.Select(id => state.RegionsById[id]).ToList();
        var affected = new EditorAffectedSlicesDto(
            null,
            updatedRegions,
            state.RegionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);
        var data = new EditorRegionOrderResult(state.RegionOrder);

        return Ok(new EditorMutationResult<EditorRegionOrderResult>(true, data, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    private EditorOptionsDto BuildOptions()
    {
        var colors = _iconColorProvider.GetAvailableColors();

        return new EditorOptionsDto(
            ReadIconNames(),
            colors?.Backgrounds ?? Array.Empty<string>(),
            colors?.Glyphs ?? Array.Empty<string>(),
            SegmentTransportModes.Options,
            new EditorAreaDefaultsDto("Area", "#ff6600"),
            new EditorTagOptionsDto(25, 8, "Letters, numbers, spaces, hyphens, and apostrophes."),
            new EditorLimitsDto(6, 1));
    }

    private string? GeneratePublicTripUrl(Guid tripId) =>
        Url.Action("View", "TripViewer", new { area = "Public", id = tripId }, Request.Scheme);

    private string? GenerateProgressPublicTripUrl(Guid tripId) =>
        Url.Action("View", "TripViewer", new { area = "Public", id = tripId, progress = 1 }, Request.Scheme);

    private IActionResult? RequireEditorUser(out string? userId)
    {
        userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (User?.IsInRole("User") != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private async Task<EditorTripStateDto> LoadEditorStateForOwnedTrip(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .Include(t => t.Tags)
            .SingleAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        var placeIds = trip.Regions.SelectMany(r => r.Places).Select(p => p.Id).ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        var visitsByPlaceId = visits
            .GroupBy(v => v.PlaceId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());

        return EditorTripStateMapper.ToEditorState(
            trip,
            visitsByPlaceId,
            BuildOptions(),
            trip.IsPublic ? GeneratePublicTripUrl(trip.Id) : null,
            trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(trip.Id) : null);
    }

    private async Task NormalizeRegionOrders(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .Where(r => r.TripId == tripId && r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var shadow = regions.FirstOrDefault(IsShadowRegion);
        if (shadow != null)
        {
            shadow.DisplayOrder = 0;
        }

        var order = 1;
        foreach (var region in regions.Where(r => !IsShadowRegion(r)))
        {
            region.DisplayOrder = order++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task NormalizeSegmentOrders(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].DisplayOrder = i;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<JsonElement?> ParseJsonBody(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static HashSet<string> BuildTripExternalImageSet(string? coverImageUrl, string? notesHtml)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(coverImageUrl))
        {
            urls.Add(coverImageUrl.Trim());
        }

        foreach (var url in HtmlHelpers.ExtractExternalImageUrls(notesHtml))
        {
            urls.Add(url);
        }

        return urls;
    }

    private static string? NormalizeOptionalUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Point? ToPoint(EditorCoordinateDto? coordinate) =>
        coordinate == null ? null : new Point(coordinate.Longitude, coordinate.Latitude) { SRID = 4326 };

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0
        && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);

    private static IActionResult ForbiddenProblem(string detail)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Type = "https://wayfarer/errors/forbidden",
            Title = "Forbidden.",
            Detail = detail
        };

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentTypes = { "application/problem+json" }
        };
    }

    private static IActionResult ValidationError(Dictionary<string, string[]> errors)
    {
        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        };

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    }

}
