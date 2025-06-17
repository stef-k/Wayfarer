using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        ModelState.Remove(nameof(model.UserId));
        ModelState.Remove(nameof(model.Region));
        
        if (Request.Form.TryGetValue("Latitude", out var latStr) &&
            Request.Form.TryGetValue("Longitude", out var lonStr) &&
            double.TryParse(latStr, out var lat) &&
            double.TryParse(lonStr, out var lon))
        {
            model.Location = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
        }

        if (!ModelState.IsValid)
            return PartialView("~/Areas/User/Views/Trip/Partials/_PlaceFormPartial.cshtml", model);

        // Detect if this is an edit
        var existing = await _dbContext.Places
            .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserId == userId);

        if (existing != null)
        {
            // ✅ Update mode
            existing.Name = model.Name;
            existing.Notes = model.Notes;
            existing.IconName = model.IconName;
            existing.MarkerColor = model.MarkerColor;
            existing.DisplayOrder = model.DisplayOrder;
            existing.Address = model.Address;
            if (model.Location != null)
                existing.Location = model.Location;
        }
        else
        {
            // ✅ Create mode
            _dbContext.Places.Add(model);
        }

        await _dbContext.SaveChangesAsync();

        // Return refreshed region
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
