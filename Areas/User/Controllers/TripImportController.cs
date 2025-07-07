using System.Security.Claims;
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
            return BadRequest("File missing");

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
    }
}