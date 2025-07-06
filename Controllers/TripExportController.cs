using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Controllers
{
    [Route("Trip/[action]/{id}")]
    public class TripExportController : BaseController
    {
        private readonly ITripExportService _exportSvc;

        public TripExportController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            ITripExportService exportSvc)
            : base(logger, dbContext)
        {
            _exportSvc = exportSvc;
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
        public async Task<IActionResult> ExportKml(Guid id)
        {
            try
            {
                var trip = await LoadAndAuthorizeAsync(id);
                var kml = _exportSvc.GenerateWayfarerKml(trip.Id);
                var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
                return File(bytes,
                    "application/vnd.google-earth.kml+xml",
                    $"Trip-{trip.Id}.kml");
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
        public async Task<IActionResult> ExportMyMapsKml(Guid id)
        {
            try
            {
                var trip = await LoadAndAuthorizeAsync(id);
                var kml = _exportSvc.GenerateMyMapsKml(trip.Id);
                var bytes = System.Text.Encoding.UTF8.GetBytes(kml);
                return File(bytes,
                    "application/vnd.google-earth.kml+xml",
                    $"Trip-Google-{trip.Id}.kml");
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
        public async Task<IActionResult> ExportPdf(Guid id)
        {
            try
            {
                var trip = await LoadAndAuthorizeAsync(id);
                var stream = await _exportSvc.GeneratePdfGuideAsync(trip.Id);
                return File(stream,
                    "application/pdf",
                    $"Trip-{trip.Id}.pdf");
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
    }
}
