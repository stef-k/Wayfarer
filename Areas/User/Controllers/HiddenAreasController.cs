using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;
using NetTopologySuite;
using Wayfarer.Localization;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class HiddenAreasController : BaseController
    {
        private readonly WebMessages _text;

        /// <summary>Creates the owner-scoped Hidden Areas controller.</summary>
        public HiddenAreasController(ILogger<BaseController> logger, ApplicationDbContext dbContext, WebMessages text)
            : base(logger, dbContext)
        {
            _text = text;
        }

        /// <summary>Displays the owner’s hidden areas.</summary>
        public async Task<IActionResult> Index()
        {
            var userId = User.Identity?.Name;
            var user = await _dbContext.Users
                .Include(u => u.HiddenAreas)
                .FirstOrDefaultAsync(u => u.UserName == userId);

            return View(user?.HiddenAreas.ToList() ?? new List<HiddenArea>());
        }

        /// <summary>Displays a blank drawing form.</summary>
        public IActionResult Create()
        {
            var model = new HiddenAreaCreateViewModel();
            return View(model);
        }

        /// <summary>Saves a new hidden area with the drawing’s coordinates.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(HiddenAreaCreateViewModel vm)
        {
            if (string.IsNullOrWhiteSpace(vm.AreaWKT) && !ModelState.ContainsKey(nameof(vm.AreaWKT)))
                ModelState.AddModelError(nameof(vm.AreaWKT), _text["HiddenAreas.DrawRequired"]);
            if (!ModelState.IsValid)
                return View(vm);

            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId))
                {
                    SetAlert("User not authenticated.", "danger", "Settings.UserUnauthenticated");
                    return RedirectToAction("Index", "Home", new { area = "" });
                }

                var polygon = (Polygon)ReadDrawingGeometry(vm.AreaWKT);
                var hiddenArea = new HiddenArea
                {
                    Name = vm.Name,
                    Description = vm.Description,
                    Area = polygon,
                    UserId = userId
                };

                _dbContext.HiddenAreas.Add(hiddenArea);
                await _dbContext.SaveChangesAsync();

                SetAlert("Hidden area added successfully.", "success", "HiddenAreas.Added");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex, "HiddenAreas.AddFailed");
                return View(vm);
            }
        }

        /// <summary>Loads an owned area for editing.</summary>
        public async Task<IActionResult> Edit(int id)
        {
            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger", "Settings.UserUnauthenticated");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(h => h.User)
                .FirstOrDefaultAsync(h => h.Id == id && h.User != null && h.User.UserName == userId);

            if (hiddenArea == null)
            {
                return NotFound();
            }

            var vm = new HiddenAreaEditViewModel
            {
                Id = hiddenArea.Id,
                Name = hiddenArea.Name,
                Description = hiddenArea.Description ?? string.Empty,
                AreaWKT = hiddenArea.Area?.AsText() ?? ""
            };

            return View(vm);
        }

        /// <summary>Saves changes to an owned area.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(HiddenAreaEditViewModel vm)
        {
            if (!ModelState.IsValid)
            {
                return View(vm);
            }

            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger", "Settings.UserUnauthenticated");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(h => h.User)
                .FirstOrDefaultAsync(h => h.Id == vm.Id && h.User != null && h.User.UserName == userId);

            if (hiddenArea == null)
            {
                return NotFound();
            }

            try
            {
                var polygon = ReadDrawingGeometry(vm.AreaWKT) as Polygon;
                if (polygon == null)
                {
                    SetAlert("Invalid area geometry.", "danger", "HiddenAreas.InvalidGeometry");
                    return View(vm);
                }
                // Validate before mutating tracked data: HandleError also saves its audit entry.
                hiddenArea.Name = vm.Name;
                hiddenArea.Description = vm.Description;
                hiddenArea.Area = polygon;

                await _dbContext.SaveChangesAsync();

                SetAlert("Hidden area updated successfully.", "success", "HiddenAreas.Updated");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                HandleError(ex, "HiddenAreas.UpdateFailed");
                return View(vm);
            }
        }


        /// <summary>Assigns the drawing forms' longitude/latitude CRS without changing coordinates.</summary>
        /// <remarks>Default only unspecified WKT; reject an explicitly different coordinate system.</remarks>
        private static Geometry ReadDrawingGeometry(string wkt)
        {
            var reader = new WKTReader(new NtsGeometryServices(new PrecisionModel(), 4326));
            var geometry = reader.Read(wkt);
            if (geometry.SRID != 4326)
                throw new ArgumentException("Hidden Areas must use longitude/latitude coordinates (SRID 4326).");

            return geometry;
        }

        /// <summary>Confirms deletion of an owned area.</summary>
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
                return NotFound();

            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger", "Settings.UserUnauthenticated");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.Id == id && m.User != null && m.User.UserName == userId);

            if (hiddenArea == null)
                return NotFound();

            return View(hiddenArea);
        }

        /// <summary>Deletes the area only when it belongs to the caller.</summary>
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger", "Settings.UserUnauthenticated");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.Id == id && m.User != null && m.User.UserName == userId);

            if (hiddenArea != null)
            {
                _dbContext.HiddenAreas.Remove(hiddenArea);
                await _dbContext.SaveChangesAsync();
                SetAlert("Hidden area deleted successfully.", "success", "HiddenAreas.Deleted");
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
