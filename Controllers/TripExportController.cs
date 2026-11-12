using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;

namespace Wayfarer.Controllers
{
    [Route("Trip/[action]/{id}")]
    public class TripExportController : BaseController
    {
        private readonly ITripExportService _exportSvc;
        private readonly SseService _sseService;

        public TripExportController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            ITripExportService exportSvc,
            SseService sseService)
            : base(logger, dbContext)
        {
            _exportSvc = exportSvc;
            _sseService = sseService;
        }

        // Helper to enforce public or owner access
        private async Task<Trip> LoadAndAuthorizeAsync(Guid id)
        {
            var trip = await _dbContext.Trips
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);
            if (trip == null)
                throw new ArgumentException($"Trip not found: {id}", nameof(id));

            Response.Headers.CacheControl = "private, no-store";
            await TripReusePolicy.RequireAsync(_dbContext, id, User.FindFirstValue(ClaimTypes.NameIdentifier), HttpContext.RequestAborted);
            if (!trip.IsPublic)
            {
                Wayfarer.Middleware.BrowserResponsePolicyMiddleware.RequirePrivateResponse(HttpContext);
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null || trip.UserId != userId)
                    throw new UnauthorizedAccessException("You are not authorized to export this trip.");
            }

            return trip;
        }

        /// <summary>Distinct authenticated owner action: public visibility cannot upgrade download authority.</summary>
        [HttpGet]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> ExportPersonalWayfarerKml(Guid id, bool includePrivatePlanning = false)
        {
            Response.Headers.CacheControl = "private, no-store";
            try
            {
                var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (owner == null) return Unauthorized();
                var download = await _exportSvc.GeneratePersonalNativeAsync(id, owner, includePrivatePlanning, HttpContext.RequestAborted);
                await TripReusePolicy.RequireAsync(_dbContext, id, owner, HttpContext.RequestAborted);
                return File(System.Text.Encoding.UTF8.GetBytes(download.Kml), "application/vnd.google-earth.kml+xml", $"{download.Name}-Wayfarer.kml");
            }
            catch (UnauthorizedAccessException) { return NotFound(); }
            catch (Exception error) when (error is InvalidOperationException or Wayfarer.Models.Planning.PlanningValidationException)
            {
                return UnprocessableEntity("The complete personal export cannot be encoded within the supported format and limits.");
            }
        }

        /// <summary>Exports native KML after access checks; serialization failures use the existing server-error handler.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportWayfarerKml(Guid id)
        {
            Trip trip;
            try
            {
                trip = await LoadAndAuthorizeAsync(id);
            }
            catch (ArgumentException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }

            // Only lookup/authentication failures above are translated into 404/403.
            string kml;
            try { kml = _exportSvc.GenerateWayfarerKml(trip.Id); await LoadAndAuthorizeAsync(id); }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (System.Data.DBConcurrencyException)
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            catch (Npgsql.PostgresException error) when (error.SqlState is "40001" or "40P01")
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            // Current delivery authorization completed above.
            var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
            return File(bytes,
                "application/vnd.google-earth.kml+xml",
                $"{trip.Name}-Wayfarer.kml");
        }

        /// <summary>Exports Google KML after access checks; serialization failures use the existing server-error handler.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportGoogleMyMapsKml(Guid id)
        {
            Trip trip;
            try
            {
                trip = await LoadAndAuthorizeAsync(id);
            }
            catch (ArgumentException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }

            // Keep serialization outside the missing-Trip and access-denial boundary.
            string kml;
            try { kml = _exportSvc.GenerateGoogleMyMapsKml(trip.Id); await LoadAndAuthorizeAsync(id); }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (System.Data.DBConcurrencyException)
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            catch (Npgsql.PostgresException error) when (error.SqlState is "40001" or "40P01")
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
            return File(bytes,
                "application/vnd.google-earth.kml+xml",
                $"{trip.Name}-Google.kml");
        }

        /// <summary>
        /// SSE endpoint for real-time PDF generation progress.
        /// Works for both public and private trips (uses same authorization as exports).
        /// </summary>
        [HttpGet]
        [Route("/Trip/ExportProgress/{id}")]
        public async Task ExportProgress(Guid id, [FromQuery] string sessionId, CancellationToken ct)
        {
            // Verify access to trip (public OR owned by user)
            try
            {
                await LoadAndAuthorizeAsync(id);
            }
            catch (ArgumentException)
            {
                Response.StatusCode = 404;
                return;
            }
            catch (UnauthorizedAccessException)
            {
                Response.StatusCode = 403;
                return;
            }

            // Channel name: unique per export session
            var channel = $"pdf-export-{id}-{sessionId}";
            await _sseService.SubscribeAsync(channel, Response, ct, enableHeartbeat: true);
        }

        [HttpGet]
        public async Task<IActionResult> ExportPdf(Guid id, [FromQuery] string? sessionId = null, CancellationToken cancellationToken = default)
        {
            Trip trip;
            try
            {
                trip = await LoadAndAuthorizeAsync(id);
            }
            catch (ArgumentException)        // only catches _not found_
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }

            // Build progress channel if sessionId provided
            string? progressChannel = null;
            if (!string.IsNullOrEmpty(sessionId))
            {
                progressChannel = $"pdf-export-{trip.Id}-{sessionId}";
            }

            // now call the exporter _outside_ of that try/catch
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
            try
            {
                var stream = await _exportSvc.GeneratePdfGuideAsync(trip.Id, progressChannel, linked.Token);
                try { await LoadAndAuthorizeAsync(id); }
                catch { await stream.DisposeAsync(); throw; }
                return File(stream, "application/pdf", $"{trip.Name}.pdf");
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (System.Data.DBConcurrencyException)
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            catch (Npgsql.PostgresException error) when (error.SqlState is "40001" or "40P01")
            { return Conflict("The Trip changed while export was waiting. Review its current availability before trying again."); }
            catch (BrowserUnavailableException)
            {
                Response.Headers.RetryAfter = "10";
                return StatusCode(503, "PDF generation is temporarily unavailable. Retry shortly.");
            }
        }
    }
}
