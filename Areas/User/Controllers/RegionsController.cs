using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;


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
        public async Task<IActionResult> Create(Guid tripId, Guid? regionId = null)
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
                IsVisible = true
            };

            return PartialView("~/Areas/User/Views/Trip/Partials/_RegionFormPartial.cshtml", model);
        }


        // POST: /User/Regions/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Region model)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Set server-only fields
            model.UserId = userId;
            ModelState.Remove(nameof(model.UserId));
            ModelState.Remove(nameof(model.Trip));

            if (!ModelState.IsValid)
                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionFormPartial.cshtml", model);

            var existing = await _dbContext.Regions
                .Where(r => r.Id == model.Id && r.UserId == userId)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                // ✏️ Update mode
                existing.Name = model.Name;
                existing.Days = model.Days;
                existing.Center = model.Center;
                existing.Boundary = model.Boundary;
                existing.NotesHtml = model.NotesHtml;
                existing.DisplayOrder = model.DisplayOrder;
                existing.IsVisible = model.IsVisible;
                existing.CoverImageUrl = model.CoverImageUrl;

                await _dbContext.SaveChangesAsync();

                // 🔁 Re-query with Places included to avoid them disappearing in UI
                var regionWithPlaces = await _dbContext.Regions
                    .Include(r => r.Places)
                    .FirstOrDefaultAsync(r => r.Id == existing.Id && r.UserId == userId);

                return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", regionWithPlaces);
            }
            else
            {
                // ➕ Create mode
                _dbContext.Regions.Add(model);
                await _dbContext.SaveChangesAsync();

                // 🔁 Re-query with Places (empty) just for consistency
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

            _dbContext.Regions.Remove(region);
            await _dbContext.SaveChangesAsync();

            return Ok(id);
        }

        [HttpPost]
        public async Task<IActionResult> SaveBoundary(Guid regionId)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                var region = await _dbContext.Regions
                    .Where(r => r.Id == regionId && r.UserId == userId)
                    .FirstOrDefaultAsync();

                if (region == null)
                    return NotFound();

                using var reader = new StreamReader(Request.Body);
                var body = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(body))
                {
                    //  Clear boundary
                    region.Boundary = null;
                    _dbContext.Update(region);
                    await _dbContext.SaveChangesAsync();
                    return Ok();
                }

                var serializer = NetTopologySuite.IO.GeoJsonSerializer.Create();
                using var stringReader = new StringReader(body);
                using var jsonReader = new JsonTextReader(stringReader);

                var feature = serializer.Deserialize<NetTopologySuite.Features.Feature>(jsonReader);

                if (feature == null || feature.Geometry == null)
                {
                    // 🧹 Clear boundary
                    region.Boundary = null;
                }
                else if (feature.Geometry is not Polygon polygon)
                {
                    return BadRequest("Submitted geometry is not a valid polygon.");
                }
                else
                {
                    region.Boundary = polygon;
                }

                _dbContext.Update(region);
                await _dbContext.SaveChangesAsync();

                return Ok();
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return StatusCode(500, "Error parsing or saving polygon geometry.");
            }
        }
    }
}