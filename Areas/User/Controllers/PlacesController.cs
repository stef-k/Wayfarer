using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class PlacesController : BaseController
{
    private readonly ILogger<PlacesController> _logger;
    private readonly ApplicationDbContext _dbContext;

    public PlacesController(ILogger<PlacesController> logger, ApplicationDbContext dbContext)
        : base(logger, dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    // GET: /User/Places/CreateOrUpdate?regionId={regionId}
    public IActionResult CreateOrUpdate(Guid regionId)
    {
        var model = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = regionId,
            DisplayOrder = 0
        };

        return PartialView("~/Areas/User/Views/Trip/Partials/_PlaceFormPartial.cshtml", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrUpdate(Place model)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        model.UserId = userId;

        // ✅ Always pull lat/lon first
        double? lat = null, lon = null;
        if (Request.Form.TryGetValue("Latitude",  out var latStr) &&
            Request.Form.TryGetValue("Longitude", out var lonStr) &&
            double.TryParse(latStr,  NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat) &&
            double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon))
        {
            lat = parsedLat;
            lon = parsedLon;
        }

        // Skip modelstate entirely
        var existing = await _dbContext.Places
            .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserId == userId);

        if (existing != null)
        {
            // Replace scalar fields
            existing.Name = model.Name;
            existing.Notes = model.Notes;
            existing.IconName = model.IconName;
            existing.MarkerColor = model.MarkerColor;
            existing.DisplayOrder = model.DisplayOrder;
            existing.Address = model.Address;

            // Replace geometry completely with new object
            if (lat.HasValue && lon.HasValue)
            {
                existing.Location = new Point(lon.Value, lat.Value) { SRID = 4326 };

                // Add: explicitly mark spatial property as modified
                _dbContext.Entry(existing)
                    .Property(p => p.Location)
                    .IsModified = true;
            }
        }
        else
        {
            if (lat.HasValue && lon.HasValue)
            {
                model.Location = new NetTopologySuite.Geometries.Point(lon.Value, lat.Value)
                {
                    SRID = 4326
                };
            }

            _dbContext.Places.Add(model);
        }

        await _dbContext.SaveChangesAsync();

        var region = await _dbContext.Regions
            .Include(r => r.Places)
            .FirstOrDefaultAsync(r => r.Id == model.RegionId && r.UserId == userId);

        if (region == null)
            return NotFound();

        return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", region);
    }


    // POST: /User/Places/Delete/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var place = await _dbContext.Places
            .Include(p => p.Region)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (place == null)
            return NotFound();

        var regionId = place.RegionId;

        _dbContext.Places.Remove(place);
        await _dbContext.SaveChangesAsync();

        // Return updated region block
        var region = await _dbContext.Regions
            .AsNoTracking() // ✅ force reload to reflect recalculated Days
            .Include(r => r.Places)
            .FirstOrDefaultAsync(r => r.Id == regionId && r.UserId == userId);

        if (region == null)
            return NotFound();

        return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", region);
    }

    // GET: /User/Places/Edit/{id}
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var place = await _dbContext.Places
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (place == null)
            return NotFound();

        return PartialView("~/Areas/User/Views/Trip/Partials/_PlaceFormPartial.cshtml", place);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder([FromBody] List<OrderDto> items)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var idToOrder = items.ToDictionary(i => i.Id, i => i.Order);

        var places = await _dbContext.Places
            .Where(p => idToOrder.Keys.Contains(p.Id) && p.UserId == userId)
            .ToListAsync();

        foreach (var p in places)
            p.DisplayOrder = idToOrder[p.Id];

        await _dbContext.SaveChangesAsync();
        return NoContent();
    }
}