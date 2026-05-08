using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>
/// MVC shell controller for the Vue/Vite Trip Editor workspace spike.
/// </summary>
[Area("User")]
[Authorize(Roles = "User")]
[Route("User/Trip")]
public sealed class TripWorkspaceController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a new workspace shell controller.
    /// </summary>
    public TripWorkspaceController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Shows the Trip Editor workspace shell for an owned trip.
    /// </summary>
    [HttpGet("Workspace/{id:guid}")]
    public async Task<IActionResult> Workspace(Guid id)
    {
        var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        var trip = await _dbContext.Trips
            .AsNoTracking()
            .Where(t => t.Id == id && t.UserId == userId)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync();

        if (trip == null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Trip Editor Workspace";
        ViewData["BodyClass"] = "container-fluid";
        ViewData["LoadLeaflet"] = false;
        ViewData["LoadQuill"] = false;

        return View("~/Areas/User/Views/Trip/Workspace.cshtml", new TripEditorWorkspaceViewModel
        {
            TripId = trip.Id,
            TripName = trip.Name,
            EditorEndpointUrl = $"/api/trips/{trip.Id}/editor",
            TilesUrl = "/Public/tiles/{z}/{x}/{y}.png"
        });
    }
}
