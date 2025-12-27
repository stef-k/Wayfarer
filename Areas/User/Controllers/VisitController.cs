using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>
/// Controller for managing user's place visit events (RUD operations).
/// Allows users to view, edit, and delete their visit history.
/// </summary>
[Area("User")]
[Authorize(Roles = "User")]
public class VisitController : BaseController
{
    public VisitController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
        : base(logger, dbContext)
    {
    }

    /// <summary>
    /// Displays all user visits in a paginated table format.
    /// </summary>
    [HttpGet]
    public IActionResult Index()
    {
        SetPageTitle("Visit History");
        return View();
    }

    /// <summary>
    /// Displays the edit form for a specific visit.
    /// </summary>
    /// <param name="id">The visit ID to edit.</param>
    /// <param name="returnUrl">Optional return URL after save.</param>
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, string? returnUrl = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var visit = await _dbContext.PlaceVisitEvents
            .Include(v => v.Place)
            .ThenInclude(p => p!.Region)
            .ThenInclude(r => r!.Trip)
            .FirstOrDefaultAsync(v => v.Id == id && v.UserId == userId);

        if (visit == null)
        {
            SetAlert("Visit not found.", "danger");
            return RedirectToAction("Index");
        }

        var viewModel = new VisitEditViewModel
        {
            Id = visit.Id,
            PlaceNameSnapshot = visit.PlaceNameSnapshot,
            TripNameSnapshot = visit.TripNameSnapshot,
            RegionNameSnapshot = visit.RegionNameSnapshot,
            TripId = visit.TripIdSnapshot,
            PlaceId = visit.PlaceId,
            ArrivedAtUtc = visit.ArrivedAtUtc,
            EndedAtUtc = visit.EndedAtUtc,
            LastSeenAtUtc = visit.LastSeenAtUtc,
            Latitude = visit.PlaceLocationSnapshot?.Y ?? 0,
            Longitude = visit.PlaceLocationSnapshot?.X ?? 0,
            IconNameSnapshot = visit.IconNameSnapshot,
            MarkerColorSnapshot = visit.MarkerColorSnapshot,
            NotesHtml = visit.NotesHtml,
            ReturnUrl = GetSafeReturnUrl(returnUrl)
        };

        await PopulateDropdowns(viewModel);
        SetPageTitle($"Edit Visit - {visit.PlaceNameSnapshot}");
        return View(viewModel);
    }

    /// <summary>
    /// Saves changes to a visit.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(VisitEditViewModel model, string? saveAction)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        if (!ModelState.IsValid)
        {
            await PopulateDropdowns(model);
            SetPageTitle($"Edit Visit - {model.PlaceNameSnapshot}");
            return View(model);
        }

        var visit = await _dbContext.PlaceVisitEvents
            .FirstOrDefaultAsync(v => v.Id == model.Id && v.UserId == userId);

        if (visit == null)
        {
            SetAlert("Visit not found or you don't have permission to edit it.", "danger");
            return RedirectToAction("Index");
        }

        // Update editable fields
        visit.ArrivedAtUtc = DateTime.SpecifyKind(model.ArrivedAtUtc, DateTimeKind.Utc);
        visit.EndedAtUtc = model.EndedAtUtc.HasValue
            ? DateTime.SpecifyKind(model.EndedAtUtc.Value, DateTimeKind.Utc)
            : null;
        visit.PlaceLocationSnapshot = new NetTopologySuite.Geometries.Point(model.Longitude, model.Latitude) { SRID = 4326 };
        visit.IconNameSnapshot = model.IconNameSnapshot;
        visit.MarkerColorSnapshot = model.MarkerColorSnapshot;
        visit.NotesHtml = model.NotesHtml;

        await _dbContext.SaveChangesAsync();

        SetAlert("Visit updated successfully.", "success");
        LogAction("UpdateVisit", $"Visit {model.Id} updated for place {model.PlaceNameSnapshot}");

        var safeReturnUrl = GetSafeReturnUrl(model.ReturnUrl);
        if (string.Equals(saveAction, "return", StringComparison.OrdinalIgnoreCase) &&
            Url.IsLocalUrl(safeReturnUrl))
        {
            return Redirect(safeReturnUrl);
        }

        return RedirectToAction("Edit", new { id = model.Id, returnUrl = safeReturnUrl });
    }

    /// <summary>
    /// Deletes a single visit.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, string? returnUrl = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var visit = await _dbContext.PlaceVisitEvents
            .FirstOrDefaultAsync(v => v.Id == id && v.UserId == userId);

        if (visit == null)
        {
            SetAlert("Visit not found.", "danger");
            return RedirectToAction("Index");
        }

        var placeName = visit.PlaceNameSnapshot;
        _dbContext.PlaceVisitEvents.Remove(visit);
        await _dbContext.SaveChangesAsync();

        SetAlert($"Visit to '{placeName}' deleted successfully.", "success");
        LogAction("DeleteVisit", $"Visit {id} deleted for place {placeName}");

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        if (Url.IsLocalUrl(safeReturnUrl))
        {
            return Redirect(safeReturnUrl);
        }

        return RedirectToAction("Index");
    }

    /// <summary>
    /// Populates icon and color dropdown options.
    /// </summary>
    private async Task PopulateDropdowns(VisitEditViewModel model)
    {
        // Icon options - fetch from API or use common defaults
        var icons = new[]
        {
            "marker", "star", "camera", "museum", "eat", "drink", "hotel",
            "info", "help", "flag", "danger", "beach", "hike", "wc", "sos", "map"
        };

        model.IconOptions = icons.Select(i => new SelectListItem
        {
            Value = i,
            Text = char.ToUpper(i[0]) + i[1..],
            Selected = i == model.IconNameSnapshot
        }).ToList();

        // Color options
        var colors = new Dictionary<string, string>
        {
            { "bg-blue", "Blue" },
            { "bg-red", "Red" },
            { "bg-green", "Green" },
            { "bg-purple", "Purple" },
            { "bg-black", "Black" }
        };

        model.ColorOptions = colors.Select(c => new SelectListItem
        {
            Value = c.Key,
            Text = c.Value,
            Selected = c.Key == model.MarkerColorSnapshot
        }).ToList();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Returns a safe local URL or the default index URL.
    /// </summary>
    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return Url.Action("Index", "Visit", new { area = "User" }) ?? "/User/Visit";
    }
}
