using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

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

            if (!trip.IsPublic)
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null || trip.UserId != userId)
                    throw new UnauthorizedAccessException("You are not authorized to export this trip.");
            }

            return trip;
        }

        [HttpGet]
        public async Task<IActionResult> ExportWayfarerKml(Guid id)
        {
            try
            {
                var trip = await LoadAndAuthorizeAsync(id);
                var kml = _exportSvc.GenerateWayfarerKml(trip.Id);
                var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
                return File(bytes,
                    "application/vnd.google-earth.kml+xml",
                    $"{trip.Name}-Wayfarer.kml");
            }
            catch (ArgumentException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportGoogleMyMapsKml(Guid id)
        {
            try
            {
                var trip = await LoadAndAuthorizeAsync(id);
                var kml = _exportSvc.GenerateGoogleMyMapsKml(trip.Id);
                var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
                return File(bytes,
                    "application/vnd.google-earth.kml+xml",
                    $"{trip.Name}-Google.kml");
            }
            catch (ArgumentException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
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
        public async Task<IActionResult> ExportPdf(Guid id, [FromQuery] string? sessionId = null)
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
            var stream = await _exportSvc.GeneratePdfGuideAsync(trip.Id, progressChannel);
            return File(stream, "application/pdf", $"{trip.Name}.pdf");
        }
    }
}
