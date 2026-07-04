using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.TripViewer;

namespace Wayfarer.Areas.Public.Controllers;

public partial class TripViewerController
{
    /// <summary>
    /// Returns read-only preview state for the future public and embed Trip Viewer.
    /// </summary>
    [HttpGet]
    [Route("/Public/TripsNext/{id}/state", Name = "PublicTripViewNextState")]
    [AllowAnonymous]
    public async Task<IActionResult> ViewNextState(Guid id, bool embed = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var trip = await _dbContext.Trips
            .Include(t => t.User)
            .Include(t => t.Tags)
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (trip == null || !trip.IsPublic)
        {
            return NotFound();
        }

        var placeIds = trip.Regions.SelectMany(r => r.Places).Select(p => p.Id).ToList();
        var canReadCounts = trip.ShareProgressEnabled || (!embed && trip.UserId == userId);
        var visitEvents = canReadCounts && placeIds.Count > 0
            ? await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == trip.UserId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
                .OrderByDescending(v => v.ArrivedAtUtc)
                .ToListAsync()
            : new List<PlaceVisitEvent>();

        return Json(TripViewerStateMapper.ToPublicState(
            trip,
            visitEvents,
            trip.UserId == userId,
            User.Identity?.IsAuthenticated == true,
            embed,
            Request.Query));
    }
}
