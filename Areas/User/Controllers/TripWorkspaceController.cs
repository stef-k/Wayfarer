using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>
/// Temporary redirect controller for the former Trip Editor workspace URL.
/// </summary>
[Area("User")]
[Authorize(Roles = "User")]
public sealed class TripWorkspaceController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a new workspace redirect controller.
    /// </summary>
    public TripWorkspaceController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Redirects owned-trip workspace requests to the canonical Trip Editor route.
    /// </summary>
    [HttpGet]
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

        return RedirectToAction("Edit", "Trip", new { area = "User", id = trip.Id });
    }
}
