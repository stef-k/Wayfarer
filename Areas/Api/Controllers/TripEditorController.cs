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
using Wayfarer.Services.ExternalRouting;
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
    private readonly ExternalRoutingCapabilityProjector? _externalRoutingCapabilities;

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
        ITripEditorGeocodeSearchService? geocodeSearch = null,
        ExternalRoutingCapabilityProjector? externalRoutingCapabilities = null)
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
        _externalRoutingCapabilities = externalRoutingCapabilities;
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

        await using var snapshot = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken) : null;
        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments).ThenInclude(s => s.FromPlace).ThenInclude(p => p!.Region)
            .Include(t => t.Segments).ThenInclude(s => s.ToPlace).ThenInclude(p => p!.Region)
            .Include(t => t.Segments).ThenInclude(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place).ThenInclude(p => p.Region)
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
            var routingCapabilities = _externalRoutingCapabilities == null
                ? new Dictionary<Guid, EditorExternalRoutingCapabilityDto>()
                : await _externalRoutingCapabilities.ProjectAsync(userId, trip.Segments.ToArray(), cancellationToken);
            return Ok(EditorTripStateMapper.ToEditorState(
                trip,
                visitsByPlaceId,
                await BuildOptionsAsync(cancellationToken),
                publicUrl,
                progressPublicUrl,
                segment => _segmentMutations.IssueEditorTokens(userId, tripId, segment),
                segment => routingCapabilities.GetValueOrDefault(segment.Id)));
        }
        catch (EditorInvalidAreaGeometryException ex)
        {
            _logger.LogError(ex, "Invalid area geometry while loading editor state for Trip {TripId}, Area {AreaId}.", ex.TripId, ex.AreaId);
            return InvalidAreaGeometryProblem(ex);
        }
    }

    /// <summary>Rejects metadata writes without client originals; the conditional action owns supported saves.</summary>
    [HttpPatch("metadata")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        if (!await _dbContext.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken))
            return NotFound();
        return new TripMetadataMutationRejected(409, "mutation-originals-required",
            "Use the protected metadata contract with client original values.").ToActionResult();
    }

    /// <summary>Rejects complete Region writes without originals; use the conditional scalar action.</summary>
    [HttpPut("regions/{regionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRegion(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        if (!await _dbContext.Regions.AsNoTracking().AnyAsync(r => r.Id == regionId && r.TripId == tripId && r.UserId == userId && r.Trip.UserId == userId, cancellationToken))
            return NotFound();
        return new TripMetadataMutationRejected(409, "mutation-originals-required",
            "Use the protected Region scalar contract with client original values.").ToActionResult();
    }

    /// <summary>
    /// Rejects unconditional Region deletion; the four-way content-confirmed POST owns all effects.
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

        if (!await _dbContext.Regions.AsNoTracking().AnyAsync(p => p.Id == regionId && p.UserId == userId && p.TripId == tripId && p.Trip.UserId == userId, cancellationToken)) return NotFound();
        if (await _dbContext.Regions.AnyAsync(r => r.Id == regionId && r.Name == "Unassigned Places", cancellationToken))
            return new TripMetadataMutationRejected(403, "forbidden", "Unassigned Places cannot be deleted.").ToActionResult();
        return new TripMetadataMutationRejected(409, "mutation-confirmation-required",
            "Use the content-bound deletion preview and confirmed POST contract.").ToActionResult();
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

        try { return Ok(await _regionMutations.OrderRegionsAsync(tripId, userId!, Request.Body, false, cancellationToken)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>
    /// Searches public geocoding through the authenticated Trip Editor backend proxy.
    /// </summary>
    [HttpPost("geocode/search")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> SearchGeocode(Guid tripId, CancellationToken cancellationToken)
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

        EditorGeocodeSearchRequestDto? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<EditorGeocodeSearchRequestDto>(
                Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
        }
        catch (JsonException)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Search request must be valid JSON." } });
        }

        var normalizedQuery = NormalizeGeocodeQuery(request?.Query);
        var validationErrors = ValidateGeocodeSearch(normalizedQuery, request?.Limit, BuildOptions().Limits.NominatimSearchLimit, out var clampedLimit);
        if (validationErrors.Count > 0)
        {
            return ValidationError(validationErrors);
        }

        if (_geocodeSearch == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var outcome = await _geocodeSearch.SearchAsync(userId!, normalizedQuery, clampedLimit, cancellationToken);
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

    private EditorOptionsDto BuildOptions(IReadOnlyList<EditorTransportModeDto>? transportModes = null)
    {
        var colors = _iconColorProvider.GetAvailableColors();

        return new EditorOptionsDto(
            ReadIconNames(),
            colors?.Backgrounds ?? Array.Empty<string>(),
            colors?.Glyphs ?? Array.Empty<string>(),
            transportModes ?? Array.Empty<EditorTransportModeDto>(),
            new EditorAreaDefaultsDto("Area", "#ff6600"),
            new EditorTagOptionsDto(25, 8, "Letters, numbers, spaces, hyphens, and apostrophes."),
            new EditorLimitsDto(6, 1));
    }

    /// <summary>Builds editor options using the active database catalog as transport authority.</summary>
    private async Task<EditorOptionsDto> BuildOptionsAsync(CancellationToken cancellationToken)
    {
        var transportModes = await new TransportProfileCatalog(_dbContext).GetEditorOptionsAsync(cancellationToken);
        return BuildOptions(transportModes);
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

    private IActionResult ToActionResult<T>(EditorRegionMutationOutcome<T> outcome) =>
        outcome.Status switch
        {
            EditorRegionMutationStatus.Success => Ok(outcome.Result),
            EditorRegionMutationStatus.NotFound => NotFound(),
            EditorRegionMutationStatus.Forbidden => ForbiddenProblem(outcome.ForbiddenDetail ?? "The operation is forbidden."),
            EditorRegionMutationStatus.ValidationFailed => ValidationError(outcome.ValidationErrors ?? new Dictionary<string, string[]>(), outcome.Code),
            EditorRegionMutationStatus.Conflict => SegmentConflict(outcome.Conflict),
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

    private IActionResult SegmentConflict(object? conflict)
    {
        if (conflict is EditorSegmentConflictDto segmentConflict && segmentConflict.ConfirmationToken != null)
            Response.Headers["X-Wayfarer-Clear-Route-Confirmation"] = segmentConflict.ConfirmationToken;
        return Conflict(conflict);
    }

    private static IActionResult ValidationError(Dictionary<string, string[]> errors, string? code = null)
    {
        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        };
        if (!string.IsNullOrWhiteSpace(code)) problem.Extensions["code"] = code;

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
        else if (query.Length > 200)
        {
            errors["q"] = new[] { "Search query must be at most 200 characters." };
        }

        if (limit.HasValue && limit.Value < 1)
        {
            errors["limit"] = new[] { "Limit must be at least 1." };
        }

        clampedLimit = Math.Clamp(limit ?? maxLimit, 1, maxLimit);
        return errors;
    }

    private static string NormalizeGeocodeQuery(string? query) =>
        string.Join(' ', (query ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

}
