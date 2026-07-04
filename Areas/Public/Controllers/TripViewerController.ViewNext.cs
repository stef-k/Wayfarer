using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.Public.Controllers;

public partial class TripViewerController
{
    /// <summary>
    /// Shows the preview Vue Trip Viewer shell for public and embed modes without changing the canonical viewer route.
    /// </summary>
    [HttpGet]
    [Route("/Public/TripsNext/{id}", Name = "PublicTripViewNext", Order = 2)]
    [AllowAnonymous]
    public async Task<IActionResult> ViewNext(Guid id, bool embed = false)
    {
        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Where(t => t.Id == id && t.IsPublic)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync();

        if (trip == null) return NotFound();

        var viewerMode = embed ? "embed" : "public";
        ViewData["Title"] = trip.Name;
        ViewData["BodyClass"] = embed ? "trip-viewer-embed-body" : "container-fluid";
        ViewData["LoadLeaflet"] = false;
        ViewData["LoadQuill"] = false;

        var settings = _settingsService.GetSettings();
        return View("~/Views/Trip/ViewNext.cshtml", new TripViewerShellViewModel
        {
            TripId = trip.Id,
            TripName = trip.Name,
            ViewerMode = viewerMode,
            ViewerStateEndpoint = embed
                ? $"/Public/TripsNext/{trip.Id}/state?embed=true"
                : $"/Public/TripsNext/{trip.Id}/state",
            PublicViewUrl = $"/Public/TripsNext/{trip.Id}",
            OpenCanonicalUrl = embed ? $"/Public/TripsNext/{trip.Id}" : null,
            TilesUrl = "/Public/tiles/{z}/{x}/{y}.png",
            TileAttribution = settings.TileProviderAttribution
        });
    }
}
