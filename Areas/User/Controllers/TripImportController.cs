using System.Security.Claims;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.User.Controllers;

[Area("User"), Authorize, Route("User/Trip/[action]")]
public class TripImportController : BaseController
{
    private const string GenericRouteReminder =
        "Imported KML routes do not contain reliable transport information. Select a transport mode for each route where needed to enable automatic duration estimates.";
    private readonly ITripImportService _svc;

    public TripImportController(
        ILogger<BaseController> logger,
        ApplicationDbContext dbContext,
        ITripImportService   svc)
        : base(logger, dbContext)
    {
        _svc = svc;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Wayfarer.Middleware.UserFileUpload]
    [RequestFormLimits(MultipartBodyLengthLimit = Wayfarer.Parsers.WayfarerKmlParser.MaximumEncodedDocumentBytes)]
    public async Task<IActionResult> Import(
        IFormFile       file,
        TripImportMode  mode = TripImportMode.Auto, string? reviewToken = null, [FromForm] string[]? choices = null)
    {
        return await HandleAsync(file, mode, reviewToken, choices, reviewOnly: false);
    }

    /// <summary>Read-only owner review shares upload limits, antiforgery and safe errors with import.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Wayfarer.Middleware.UserFileUpload]
    [RequestFormLimits(MultipartBodyLengthLimit = Wayfarer.Parsers.WayfarerKmlParser.MaximumEncodedDocumentBytes)]
    public Task<IActionResult> ReviewImport(IFormFile file) => HandleAsync(file, TripImportMode.Upsert, null, null, reviewOnly: true);

    /// <summary>Preserves the bounded response contract without reflecting untrusted file/exception text.</summary>
    private async Task<IActionResult> HandleAsync(IFormFile file, TripImportMode mode, string? reviewToken, string[]? choices, bool reviewOnly)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (file is null || file.Length == 0)
            return ImportError(StatusCodes.Status400BadRequest, "invalid_file", "Select a KML file to import.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        await using var stream = file.OpenReadStream();

        try
        {
            if (reviewOnly) return Json(new { status = "review", review = await _svc.ReviewNativeAsync(stream, userId, HttpContext.RequestAborted) });
            if (reviewToken != null && mode != TripImportMode.Upsert)
                return ImportError(400, "import_choices_invalid", "The selected import choices are invalid.");
            var result = reviewToken == null
                ? await _svc.ImportWayfarerKmlAsync(stream, userId, mode, HttpContext.RequestAborted)
                : await _svc.ImportReviewedNativeAsync(stream, userId, new(reviewToken, choices ?? []), HttpContext.RequestAborted);
            var redirectUrl = $"/User/Trip/Edit/{result.TripId:D}";
            if (result.IsGenericWithRoutes)
            {
                TempData["AlertType"] = "info";
                TempData["AlertMessage"] = GenericRouteReminder;
            }
            return Json(new
            {
                status = "success",
                tripId = result.TripId,
                redirectUrl,
                notices = result.Notices
            });
        }
        catch (TripDuplicateException ex)          // <- see section 2
        {
            return Json(new { status = "duplicate", tripId = ex.TripId });
        }
        catch (NativeImportReviewException ex)
        {
            Response.Headers.CacheControl = "private, no-store";
            return ImportError(StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
        catch (TripImportValidationException ex)
        {
            _logger.LogWarning("Trip import validation failed for user {UserId} Failure type: {ExceptionType}; request {RequestId}", userId, ex.GetType().Name, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status422UnprocessableEntity, "validation_failed", "The import contains invalid data.");
        }
        catch (RouteGeometryBudgetException ex)
        {
            _logger.LogWarning("Generic route import rejected with code {ImportCode} for user {UserId}", ex.Code, userId);
            return ImportError(StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Trip import was cancelled for user {UserId}", userId);
            return ImportError(499, "import_cancelled", "The import was cancelled.");
        }
        catch (FormatException ex)
        {
            _logger.LogWarning("Trip import KML parsing failed for user {UserId} Failure type: {ExceptionType}; request {RequestId}", userId, ex.GetType().Name, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status400BadRequest, "invalid_kml", "The selected file is not a valid KML import.");
        }
        catch (XmlException ex)
        {
            _logger.LogWarning("Trip import XML parsing failed for user {UserId} Failure type: {ExceptionType}; request {RequestId}", userId, ex.GetType().Name, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status400BadRequest, "invalid_kml", "The selected file is not a valid KML import.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Trip import could not be completed for user {UserId} Failure type: {ExceptionType}; request {RequestId}", userId, ex.GetType().Name, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status400BadRequest, "validation_failed", "The import cannot be applied to this trip.");
        }
        catch (Exception ex)
        {
            _logger.LogError("Trip import failed for user {UserId}; failure type {ExceptionType}; request {RequestId}",
                userId, ex.GetType().Name, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status500InternalServerError, "import_outcome_unknown", "The import outcome could not be confirmed. Check My Trips before starting another import.");
        }
    }

    /// <summary>Returns the stable, safe JSON contract for a failed import.</summary>
    private static JsonResult ImportError(int statusCode, string code, string message) =>
        new(new { status = "error", code, message }) { StatusCode = statusCode, ContentType = "application/json" };
}
