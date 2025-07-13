using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class AreasController : BaseController
    {
        private readonly ILogger<AreasController> _logger;
        private readonly ApplicationDbContext _db;

        public AreasController(ILogger<AreasController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
            _logger = logger;
            _db = dbContext;
        }

        /// <summary>
        /// Renders a form to create a new Area within the specified Region.
        /// </summary>
        /// <param name="regionId">Parent Region ID</param>
        public IActionResult CreateOrUpdate(Guid regionId)
        {
            var model = new Area
            {
                Id = Guid.NewGuid(),
                RegionId = regionId,
                Name = "Area",
                FillHex = "#ff6600"
            };
            return PartialView("~/Areas/User/Views/Trip/Partials/_AreaFormPartial.cshtml", model);
        }

        /// <summary>
        /// Handles both creation and update of an Area, parsing the GeoJSON polygon from request body.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateOrUpdate(Area model)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Remove the automatic binder error for Geometry
            ModelState.Remove(nameof(model.Geometry));
            // Remove the automatic binder error for the Region navigation
            ModelState.Remove(nameof(model.Region));

            // Pull the raw JSON from the hidden input
            var geomJson = Request.Form["Geometry"].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(geomJson) && geomJson != "null")
            {
                try
                {
                    var reader = new GeoJsonReader();
                    var geometry = reader.Read<Geometry>(geomJson);
                    if (geometry is Polygon poly)
                    {
                        model.Geometry = poly;
                    }
                    else
                    {
                        ModelState.AddModelError(
                            nameof(model.Geometry),
                            "You must draw a single polygon (no multi-polygons).");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Area.Geometry JSON");
                    ModelState.AddModelError(
                        nameof(model.Geometry),
                        "Unable to parse the drawn area. Please redraw and try again.");
                }
            }
            else
            {
                ModelState.AddModelError(
                    nameof(model.Geometry),
                    "Please draw an area before saving.");
            }

            // If anything’s invalid, return the form partial so the user sees the validation-summary
            if (!ModelState.IsValid)
            {
                var errors = ModelState.SelectMany(m => m.Value.Errors).Select(m => m.ErrorMessage);

                foreach (var error in errors)
                {
                    Console.WriteLine($"==== Error: {error}");
                }

                return PartialView(
                    "~/Areas/User/Views/Trip/Partials/_AreaFormPartial.cshtml",
                    model);
            }

            // ─── Now model.Geometry is a real Polygon ───

            // ④ Upsert your Area entity
            var existing = await _db.Areas
                .FirstOrDefaultAsync(a => a.Id == model.Id && a.Region.UserId == userId);
            if (existing != null)
            {
                existing.Name = model.Name;
                existing.Notes = model.Notes;
                existing.FillHex = model.FillHex;
                existing.Geometry = model.Geometry;
                _db.Entry(existing).State = EntityState.Modified;
            }
            else
            {
                _db.Areas.Add(model);
            }

            await _db.SaveChangesAsync();

            // ⑤ Re-load the Region (with its new Area list) and return its partial
            var region = await _db.Regions
                .Where(r => r.Id == model.RegionId && r.UserId == userId)
                .Include(r => r.Places)
                .Include(r => r.Areas)
                .FirstOrDefaultAsync();

            return PartialView(
                "~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml",
                region);
        }


        /// <summary>
        /// Returns the edit form for an existing Area.
        /// </summary>
        public async Task<IActionResult> Edit(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var area = await _db.Areas
                .Include(a => a.Region)
                .FirstOrDefaultAsync(a => a.Id == id && a.Region.UserId == userId);
            if (area == null)
                return NotFound();

            return PartialView("~/Areas/User/Views/Trip/Partials/_AreaFormPartial.cshtml", area);
        }

        /// <summary>
        /// Deletes the specified Area and returns updated Region markup.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var area = await _db.Areas
                .Include(a => a.Region)
                .FirstOrDefaultAsync(a => a.Id == id && a.Region.UserId == userId);

            if (area == null)
                return NotFound();

            var regionId = area.RegionId;
            _db.Areas.Remove(area);
            await _db.SaveChangesAsync();

            var region = await _db.Regions
                .AsNoTracking()
                .Include(r => r.Places)
                .Include(r => r.Areas)
                .FirstOrDefaultAsync(r => r.Id == regionId && r.UserId == userId);

            return PartialView("~/Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml", region);
        }

        /// <summary>
        /// Reorders Areas via drag‑and‑drop, updating DisplayOrder.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reorder([FromBody] List<OrderDto> items)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var idToOrder = items.ToDictionary(i => i.Id, i => i.Order);
            var areas = await _db.Areas
                .Where(a => idToOrder.Keys.Contains(a.Id) && a.Region.UserId == userId)
                .ToListAsync();

            areas.ForEach(a => a.DisplayOrder = idToOrder[a.Id]);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}