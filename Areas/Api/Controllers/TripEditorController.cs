using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
public sealed partial class TripEditorController : ControllerBase
{
    private const string PublicTripViewRouteName = "PublicTripView";

    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IIconColorProvider _iconColorProvider;
    private readonly ITripMapThumbnailGenerator _thumbnailGenerator;
    private readonly ICacheWarmupScheduler _warmupScheduler;
    private readonly ITripTagService _tripTagService;
    private readonly TripEditorRegionMutationService _regionMutations;
    private readonly TripEditorPlaceMutationService _placeMutations;
    private readonly TripEditorAreaMutationService _areaMutations;
    private readonly TripEditorSegmentMutationService _segmentMutations;
    private readonly ITripEditorGeocodeSearchService? _geocodeSearch;
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
        ITripTagService tripTagService,
        TripEditorRegionMutationService regionMutations,
        TripEditorPlaceMutationService placeMutations,
        TripEditorAreaMutationService areaMutations,
        TripEditorSegmentMutationService segmentMutations,
        ILogger<TripEditorController> logger,
        ITripEditorGeocodeSearchService? geocodeSearch = null)
    {
        _dbContext = dbContext;
        _environment = environment;
        _iconColorProvider = iconColorProvider;
        _thumbnailGenerator = thumbnailGenerator;
        _warmupScheduler = warmupScheduler;
        _tripTagService = tripTagService;
        _regionMutations = regionMutations;
        _placeMutations = placeMutations;
        _areaMutations = areaMutations;
        _segmentMutations = segmentMutations;
        _geocodeSearch = geocodeSearch;
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

        var outcome = await _regionMutations.CreateRegionAsync(tripId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
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

        var outcome = await _regionMutations.UpdateRegionAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
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

        var outcome = await _regionMutations.DeleteRegionAsync(tripId, regionId, userId!, cancellationToken);
        return ToActionResult(outcome);
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

        var outcome = await _regionMutations.OrderRegionsAsync(tripId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Searches public geocoding through the authenticated Trip Editor backend proxy.
    /// </summary>
    [HttpGet("geocode/search")]
    public async Task<IActionResult> SearchGeocode(Guid tripId, [FromQuery(Name = "q")] string? query, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Where(t => t.Id == tripId)
            .Select(t => new { t.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        if (!string.Equals(trip.UserId, userId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var normalizedQuery = query?.Trim() ?? string.Empty;
        var validationErrors = ValidateGeocodeSearch(normalizedQuery, limit, BuildOptions().Limits.NominatimSearchLimit, out var clampedLimit);
        if (validationErrors.Count > 0)
        {
            return ValidationError(validationErrors);
        }

        if (_geocodeSearch == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var outcome = await _geocodeSearch.SearchAsync(normalizedQuery, clampedLimit, cancellationToken);
        return outcome.Status switch
        {
            TripEditorGeocodeSearchStatus.Success => Ok(outcome.Response),
            TripEditorGeocodeSearchStatus.LocalRateLimited => StatusCode(StatusCodes.Status429TooManyRequests),
            TripEditorGeocodeSearchStatus.ProviderRateLimited => StatusCode(StatusCodes.Status429TooManyRequests),
            TripEditorGeocodeSearchStatus.ProviderMalformed => StatusCode(StatusCodes.Status502BadGateway),
            TripEditorGeocodeSearchStatus.ProviderUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable),
            TripEditorGeocodeSearchStatus.ProviderTimeout => StatusCode(StatusCodes.Status504GatewayTimeout),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
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

    /// <summary>
    /// Generates absolute public trip links through the named attribute route to avoid conventional area fallback URLs.
    /// </summary>
    private string? GeneratePublicTripUrl(Guid tripId, int? progress = null)
    {
        object values = progress.HasValue
            ? new { id = tripId, progress = progress.Value }
            : new { id = tripId };

        return Url.RouteUrl(new UrlRouteContext
        {
            RouteName = PublicTripViewRouteName,
            Values = values,
            Protocol = Request.Scheme
        });
    }

    private string? GenerateProgressPublicTripUrl(Guid tripId) =>
        GeneratePublicTripUrl(tripId, progress: 1);

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

    private IActionResult ToActionResult<T>(EditorRegionMutationOutcome<T> outcome) =>
        outcome.Status switch
        {
            EditorRegionMutationStatus.Success => Ok(outcome.Result),
            EditorRegionMutationStatus.NotFound => NotFound(),
            EditorRegionMutationStatus.Forbidden => ForbiddenProblem(outcome.ForbiddenDetail ?? "The operation is forbidden."),
            EditorRegionMutationStatus.ValidationFailed => ValidationError(outcome.ValidationErrors ?? new Dictionary<string, string[]>()),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };

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

    private static Dictionary<string, string[]> ValidateGeocodeSearch(string query, int? limit, int maxLimit, out int clampedLimit)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(query))
        {
            errors["q"] = new[] { "Search query is required." };
        }
        else if (query.Length < 3)
        {
            errors["q"] = new[] { "Search query must be at least 3 characters." };
        }

        if (limit.HasValue && limit.Value < 1)
        {
            errors["limit"] = new[] { "Limit must be at least 1." };
        }

        clampedLimit = Math.Clamp(limit ?? maxLimit, 1, maxLimit);
        return errors;
    }

}
