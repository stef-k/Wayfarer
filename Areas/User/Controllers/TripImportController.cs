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
    public async Task<IActionResult> Import(
        IFormFile       file,
        TripImportMode  mode = TripImportMode.Auto)
    {
        if (file is null || file.Length == 0)
            return ImportError(StatusCodes.Status400BadRequest, "invalid_file", "Select a KML file to import.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        await using var stream = file.OpenReadStream();

        try
        {
            var tripId = await _svc.ImportWayfarerKmlAsync(stream, userId, mode);
            return RedirectToAction("Edit", "Trip", new { id = tripId });
        }
        catch (TripDuplicateException ex)          // <- see section 2
        {
            return Json(new { status = "duplicate", tripId = ex.TripId });
        }
        catch (TripImportValidationException ex)
        {
            _logger.LogWarning(ex, "Trip import validation failed for user {UserId}", userId);
            return ImportError(StatusCodes.Status422UnprocessableEntity, "validation_failed", "The import contains invalid data.");
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Trip import KML parsing failed for user {UserId}", userId);
            return ImportError(StatusCodes.Status400BadRequest, "invalid_kml", "The selected file is not a valid KML import.");
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Trip import XML parsing failed for user {UserId}", userId);
            return ImportError(StatusCodes.Status400BadRequest, "invalid_kml", "The selected file is not a valid KML import.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Trip import could not be completed for user {UserId}", userId);
            return ImportError(StatusCodes.Status400BadRequest, "validation_failed", "The import cannot be applied to this trip.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trip import failed for user {UserId}; trace {TraceIdentifier}", userId, HttpContext.TraceIdentifier);
            return ImportError(StatusCodes.Status500InternalServerError, "import_failed", "Import failed. Please try again.");
        }
    }

    /// <summary>Returns the stable, safe JSON contract for a failed import.</summary>
    private static JsonResult ImportError(int statusCode, string code, string message) =>
        new(new { status = "error", code, message }) { StatusCode = statusCode, ContentType = "application/json" };
}
