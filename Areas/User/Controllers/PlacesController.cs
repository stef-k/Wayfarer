using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class PlacesController : BaseController
{
    private new readonly ILogger<PlacesController> _logger;
    private new readonly ApplicationDbContext _dbContext;
    private readonly ReverseGeocodingService _reverseGeocodingService;

    public PlacesController(ILogger<PlacesController> logger, ApplicationDbContext dbContext, ReverseGeocodingService reverseGeocodingService)
        : base(logger, dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
        _reverseGeocodingService = reverseGeocodingService;
    }

    // GET: /User/Places/CreateOrUpdate?regionId={regionId}
    public async Task<IActionResult> CreateOrUpdate(Guid regionId)
    {
        var model = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = regionId
        };

        ViewData["AllRegions"] = await _dbContext.Regions
            .Where(r => r.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier))
            .OrderBy(r => r.DisplayOrder)
            .ToListAsync();


        return PartialView("~/Areas/User/Views/Trip/Partials/_PlaceFormPartial.cshtml", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrUpdate(Place model)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        model.UserId = userId;

        // Parse coordinates from form using invariant culture
        double? lat = null, lon = null;
        if (Request.Form.TryGetValue("Latitude", out var latStr) &&
            Request.Form.TryGetValue("Longitude", out var lonStr) &&
            double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat) &&
            double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon))
        {
            lat = parsedLat;
            lon = parsedLon;
        }

        var existing = await _dbContext.Places
            .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserId == userId);

        if (existing != null)
        {
            existing.Name = model.Name;
            existing.Notes = model.Notes;
            existing.IconName = model.IconName;
            existing.MarkerColor = model.MarkerColor;
            existing.Address = model.Address; // may be overwritten below

            if (Request.Form.TryGetValue("RegionIdOverride", out var regionOverride) &&
                Guid.TryParse(regionOverride, out var newRegionId))
            {
                model.RegionId = newRegionId;
            }

            if (existing.RegionId != model.RegionId)
            {
                existing.RegionId = model.RegionId;
                _dbContext.Entry(existing).Property(p => p.RegionId).IsModified = true;
                
                var next = await _dbContext.Places
                    .Where(p => p.RegionId == model.RegionId)
                    .MaxAsync(p => (int?)p.DisplayOrder) ?? -1;
                existing.DisplayOrder = next + 1;
            }

            if (lat.HasValue && lon.HasValue)
            {
                existing.Location = new Point(lon.Value, lat.Value) { SRID = 4326 };
                _dbContext.Entry(existing).Property(p => p.Location).IsModified = true;

                var user = await _dbContext.ApplicationUsers
                    .Include(u => u.ApiTokens)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                var apiToken = user?.ApiTokens.FirstOrDefault(t => t.Name == "Mapbox");
                if (apiToken != null)
                {
                    var locationInfo = await _reverseGeocodingService.GetReverseGeocodingDataAsync(
                        lat.Value, lon.Value, apiToken.Token, apiToken.Name);

                    existing.Address = locationInfo.FullAddress;
                }
            }
        }
        else
        {
            var next = await _dbContext.Places
                .Where(p => p.RegionId == model.RegionId)
                .MaxAsync(p => (int?)p.DisplayOrder) ?? -1;
            model.DisplayOrder = next + 1;
            
            if (lat.HasValue && lon.HasValue)
            {
                model.Location = new Point(lon.Value, lat.Value) { SRID = 4326 };

                var user = await _dbContext.ApplicationUsers
                    .Include(u => u.ApiTokens)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                var apiToken = user?.ApiTokens.FirstOrDefault(t => t.Name == "Mapbox");
                if (apiToken != null)
                {
                    var locationInfo = await _reverseGeocodingService.GetReverseGeocodingDataAsync(
                        lat.Value, lon.Value, apiToken.Token, apiToken.Name);

                    model.Address = locationInfo.FullAddress;
                }
            }

            _dbContext.Places.Add(model);
        }

        await _dbContext.SaveChangesAsync();

        var region = await _dbContext.Regions
            .Include(r => r.Places)
            .FirstOrDefaultAsync(r => r.Id == model.RegionId && r.UserId == userId);

        if (region == null)
            return NotFound();

        ViewData["AllRegions"] = await _dbContext.Regions
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ToListAsync();

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

        ViewData["AllRegions"] = await _dbContext.Regions
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ToListAsync();

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
