using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private static readonly Regex DataImageSourceRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*data:image/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    public async Task<IActionResult> PatchMetadata(Guid tripId, [FromBody] JsonElement request, CancellationToken cancellationToken)
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

        if (!TryParseMetadataRequest(request, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        var trip = await _dbContext.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
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

    private static bool TryParseMetadataRequest(
        JsonElement request,
        out EditorTripMetadataUpdateRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorTripMetadataUpdateRequest(string.Empty, null, false, null, null, null);

        if (request.ValueKind != JsonValueKind.Object)
        {
            errors[""] = new[] { "Metadata update request must be a JSON object." };
            return false;
        }

        var name = ReadRequiredString(request, "name", errors);
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var isPublic = ReadRequiredBoolean(request, "isPublic", errors);
        var coverImage = ReadRequiredCoverImage(request, errors);
        var center = ReadRequiredCenter(request, errors);
        var zoom = ReadRequiredZoom(request, errors);

        ValidateName(name, errors);
        ValidateCoverImage(coverImage?.RawUrl, errors);
        ValidateNotesHtml(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorTripMetadataUpdateRequest(name!, notesHtml, isPublic!.Value, coverImage, center, zoom);
        return true;
    }

    private static string? ReadRequiredString(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors[name] = new[] { "The field must be a string." };
            return null;
        }

        return property.GetString();
    }

    private static string? ReadRequiredNullableString(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors[name] = new[] { "The field must be a string or null." };
            return null;
        }

        return property.GetString();
    }

    private static bool? ReadRequiredBoolean(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors[name] = new[] { "The field must be a boolean." };
            return null;
        }

        return property.GetBoolean();
    }

    private static EditorImageUpdateRequest? ReadRequiredCoverImage(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("coverImage", out var property))
        {
            errors["coverImage"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["coverImage"] = new[] { "The field must be an object or null." };
            return null;
        }

        if (!property.TryGetProperty("rawUrl", out var rawUrlProperty))
        {
            errors["coverImage.rawUrl"] = new[] { "The field is required." };
            return null;
        }

        if (rawUrlProperty.ValueKind == JsonValueKind.Null)
        {
            return new EditorImageUpdateRequest(null);
        }

        if (rawUrlProperty.ValueKind != JsonValueKind.String)
        {
            errors["coverImage.rawUrl"] = new[] { "The field must be a string or null." };
            return null;
        }

        return new EditorImageUpdateRequest(rawUrlProperty.GetString());
    }

    private static EditorCoordinateDto? ReadRequiredCenter(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("center", out var property))
        {
            errors["center"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["center"] = new[] { "The field must be an object or null." };
            return null;
        }

        var latitude = ReadRequiredDouble(property, "latitude", "center.latitude", errors);
        var longitude = ReadRequiredDouble(property, "longitude", "center.longitude", errors);
        if (latitude is < -90 or > 90)
        {
            errors["center.latitude"] = new[] { "Latitude must be between -90 and 90." };
        }

        if (longitude is < -180 or > 180)
        {
            errors["center.longitude"] = new[] { "Longitude must be between -180 and 180." };
        }

        return latitude.HasValue && longitude.HasValue ? new EditorCoordinateDto(latitude.Value, longitude.Value) : null;
    }

    private static int? ReadRequiredZoom(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("zoom", out var property))
        {
            errors["zoom"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var zoom))
        {
            errors["zoom"] = new[] { "Zoom must be an integer from 0 through 19." };
            return null;
        }

        if (zoom is < 0 or > 19)
        {
            errors["zoom"] = new[] { "Zoom must be an integer from 0 through 19." };
        }

        return zoom;
    }

    private static double? ReadRequiredDouble(JsonElement request, string propertyName, string fieldKey, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(propertyName, out var property))
        {
            errors[fieldKey] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            errors[fieldKey] = new[] { "The field must be a finite number." };
            return null;
        }

        return value;
    }

    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = new[] { "Name is required." };
        }
    }

    private static void ValidateCoverImage(string? rawUrl, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["coverImage.rawUrl"] = new[] { "Cover image URL must be an absolute HTTP or HTTPS URL." };
        }
    }

    private static void ValidateNotesHtml(string? notesHtml, Dictionary<string, string[]> errors)
    {
        if (!string.IsNullOrEmpty(notesHtml) && DataImageSourceRegex.IsMatch(notesHtml))
        {
            errors["notesHtml"] = new[] { "Notes images must use external image URLs, not data:image sources." };
        }
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
