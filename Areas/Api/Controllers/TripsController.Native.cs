using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Parsers;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Downloads a complete owned native v5 file; personal planning is separately explicit and defaults off.</summary>
    [HttpGet("{id:guid}/native")]
    public async Task<IActionResult> DownloadNative(Guid id, [FromServices] ITripExportService exports,
        bool includePrivatePlanning = false, CancellationToken token = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var owner = await authority.ResolveAsync(HttpContext, token);
        if (owner == null) return Unauthorized();
        try
        {
            var result = await exports.GeneratePersonalNativeForBearerAsync(id, owner.Id, includePrivatePlanning,
                ct => authority.RevalidateMutationAsync(HttpContext, owner.Id, ct), token);
            return File(System.Text.Encoding.UTF8.GetBytes(result.Kml), "application/vnd.google-earth.kml+xml", $"{result.Name}-Wayfarer.kml");
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (Exception error) when (error is InvalidOperationException or Wayfarer.Models.Planning.PlanningValidationException)
        { return UnprocessableEntity(new { code = "native-export-invalid", detail = "The complete native file cannot be encoded within supported limits." }); }
    }

    /// <summary>Bearer multipart native import uses existing file limits and the same atomic importer, never cookie authority.</summary>
    [HttpPost("native/import"), Wayfarer.Middleware.UserFileUpload]
    [RequestFormLimits(MultipartBodyLengthLimit = WayfarerKmlParser.MaximumEncodedDocumentBytes)]
    public Task<IActionResult> ImportNative(IFormFile file, [FromServices] ITripImportService imports,
        [FromServices] ITripMapThumbnailGenerator thumbnails, [FromServices] ICacheWarmupScheduler warmup,
        [FromForm] TripImportMode mode = TripImportMode.Auto, [FromForm] string? reviewToken = null,
        [FromForm] string[]? choices = null, CancellationToken token = default) => NativeImportRequest(file, async (stream, owner, check) =>
    {
        var result = await imports.ImportNativeForBearerAsync(stream, owner, mode,
            reviewToken == null ? null : new(reviewToken, choices ?? []), check, token);
        var updatedAt = await _dbContext.Trips.AsNoTracking().Where(t => t.Id == result.TripId).Select(t => t.UpdatedAt).SingleAsync(token);
        thumbnails.InvalidateThumbnails(result.TripId, updatedAt);
        await warmup.ScheduleWarmupAsync(result.TripId);
        return new { status = "success", result.TripId, result.Notices };
    }, token);

    /// <summary>Previews native replacement with the same byte-bound permit and complete protected-field choices as Web.</summary>
    [HttpPost("native/review"), Wayfarer.Middleware.UserFileUpload]
    [RequestFormLimits(MultipartBodyLengthLimit = WayfarerKmlParser.MaximumEncodedDocumentBytes)]
    public Task<IActionResult> ReviewNative(IFormFile file, [FromServices] ITripImportService imports, CancellationToken token) =>
        NativeImportRequest(file, async (stream, owner, check) => new { status = "review", review = await imports.ReviewNativeForBearerAsync(stream, owner, check, token) }, token);

    private async Task<IActionResult> NativeImportRequest(IFormFile file,
        Func<Stream, string, Func<CancellationToken, Task<bool>>, Task<object>> action, CancellationToken token) =>
        await CompositionRequest(async (owner, check) =>
        {
            if (file == null || file.Length == 0 || file.Length > WayfarerKmlParser.MaximumEncodedDocumentBytes)
                throw new TripMetadataMutationRejected(400, "invalid_file", "Select a native KML file within the supported limit.");
            await using var stream = file.OpenReadStream();
            try { return await action(stream, owner, check); }
            catch (TripDuplicateException duplicate) { return new { status = "duplicate", tripId = duplicate.TripId }; }
            catch (NativeImportOutcomeUnknownException) { throw new TripMetadataMutationRejected(503, "native-import-unconfirmed", "The import could not be confirmed. Inspect My Trips before starting another import."); }
            catch (NativeImportReviewException error) { throw new TripMetadataMutationRejected(409, error.Code, error.Message); }
            catch (UnauthorizedAccessException) { throw new TripMetadataMutationRejected(401, "native-authority-invalid", "The incoming credential is unavailable."); }
            catch (Exception error) when (error is TripImportValidationException or FormatException or System.Xml.XmlException or InvalidOperationException)
            { throw new TripMetadataMutationRejected(422, "native-import-invalid", "The native import cannot be applied. Review its complete content and current replacement choices."); }
        }, token);
}
