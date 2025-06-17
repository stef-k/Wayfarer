using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class RegionsController : BaseController
    {
        public RegionsController(ILogger<RegionsController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
        }

        // GET: /User/Regions/Create?tripId={tripId}&regionId={regionId}
        public async Task<IActionResult> CreateOrUpdate(Guid tripId, Guid? regionId = null)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (regionId.HasValue)
            {
                // Editing existing region
                var region = await _dbContext.Regions
                    .Where(r => r.Id == regionId.Value && r.TripId == tripId && r.UserId == userId)
                    .FirstOrDefaultAsync();

                if (region == null)
                    return NotFound();

                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionFormPartial.cshtml", region);
            }

            // Creating new region
            var model = new Region
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                DisplayOrder = 0,
            };

            return PartialView("~/Areas/User/Views/Trip/Partials/_RegionFormPartial.cshtml", model);
        }


        // POST: /User/Regions/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateOrUpdate(Region model)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            model.UserId = userId;
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.Trip));

            if (Request.Form.TryGetValue("CenterLat", out var latStr) &&
                Request.Form.TryGetValue("CenterLon", out var lonStr) &&
                double.TryParse(latStr, out var lat) &&
                double.TryParse(lonStr, out var lon))
            {
                model.Center = new Point(lon, lat) { SRID = 4326 };
            }
            
            if (!ModelState.IsValid)
                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionFormPartial.cshtml", model);

            var existing = await _dbContext.Regions
                .Where(r => r.Id == model.Id && r.UserId == userId)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                existing.Name = model.Name;
                existing.Center = model.Center;
                existing.Notes = model.Notes;
                existing.DisplayOrder = model.DisplayOrder;
                existing.CoverImageUrl = model.CoverImageUrl;

                _dbContext.Entry(existing).State = EntityState.Modified;
                await _dbContext.SaveChangesAsync();
                
                var updated = await _dbContext.Regions
                    .Include(r => r.Places)
                    .FirstOrDefaultAsync(r => r.Id == existing.Id && r.UserId == userId);

                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", updated);
            }
            else
            {
                _dbContext.Regions.Add(model);
                await _dbContext.SaveChangesAsync();

                var newRegion = await _dbContext.Regions
                    .Include(r => r.Places)
                    .FirstOrDefaultAsync(r => r.Id == model.Id && r.UserId == userId);

                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", newRegion);
            }
        }


        // GET: /User/Regions/GetItemPartial?regionId={regionId}
        public async Task<IActionResult> GetItemPartial(Guid regionId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var region = await _dbContext.Regions
                .Include(r => r.Places)
                .FirstOrDefaultAsync(r => r.Id == regionId && r.UserId == userId);

            if (region == null)
                return NotFound();

            return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", region);
        }

        // POST: /User/Regions/Delete/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var region = await _dbContext.Regions
                .Where(r => r.Id == id && r.UserId == userId)
                .FirstOrDefaultAsync();

            if (region == null)
                return NotFound();

            var tripId = region.TripId;

            _dbContext.Regions.Remove(region);
            await _dbContext.SaveChangesAsync();

            return Ok(id);
        }
        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reorder([FromBody] List<OrderDto> items)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var idToOrder = items.ToDictionary(i => i.Id, i => i.Order);

            var regions = await _dbContext.Regions
                .Where(p => idToOrder.Keys.Contains(p.Id) && p.UserId == userId)
                .ToListAsync();

            foreach (var p in regions)
                p.DisplayOrder = idToOrder[p.Id];

            await _dbContext.SaveChangesAsync();
            return NoContent();
        }

    }
}