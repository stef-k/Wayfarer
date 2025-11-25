using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;
using NetTopologySuite;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class HiddenAreasController : BaseController
    {
        public HiddenAreasController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
        }

        // GET: User/HiddenAreas
        public async Task<IActionResult> Index()
        {
            var userId = User.Identity?.Name;
            var user = await _dbContext.Users
                .Include(u => u.HiddenAreas)
                .FirstOrDefaultAsync(u => u.UserName == userId);

            return View(user?.HiddenAreas.ToList() ?? new List<HiddenArea>());
        }

        // GET: User/HiddenAreas/Create
        public IActionResult Create()
        {
            var model = new HiddenAreaCreateViewModel();
            return View(model);
        }


        // POST: User/HiddenAreas/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(HiddenAreaCreateViewModel vm)
        {
            if (!ModelState.IsValid || string.IsNullOrWhiteSpace(vm.AreaWKT))
            {
                ModelState.AddModelError("AreaWKT", "Please draw a polygon area.");
                return View(vm);
            }

            try
            {
                var reader = new WKTReader();
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId))
                {
                    SetAlert("User not authenticated.", "danger");
                    return RedirectToAction("Index", "Home", new { area = "" });
                }

                var polygon = (Polygon)reader.Read(vm.AreaWKT);
                var hiddenArea = new HiddenArea
                {
                    Name = vm.Name,
                    Description = vm.Description,
                    Area = polygon,
                    UserId = userId
                };

                _dbContext.HiddenAreas.Add(hiddenArea);
                await _dbContext.SaveChangesAsync();

                SetAlert("Hidden area added successfully.", "success");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                SetAlert("Failed to add hidden area.", "error");
                HandleError(ex);
                return View(vm);
            }
        }

// GET Edit
        public async Task<IActionResult> Edit(int id)
        {
            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
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

// POST Edit
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
                SetAlert("User not authenticated.", "danger");
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
                hiddenArea.Name = vm.Name;
                hiddenArea.Description = vm.Description;

                var geometryServices = NtsGeometryServices.Instance;
                var factory = geometryServices.CreateGeometryFactory(4326);
                var reader = new WKTReader(geometryServices);
                var polygon = reader.Read(vm.AreaWKT) as Polygon;
                if (polygon == null)
                {
                    SetAlert("Invalid area geometry.", "error");
                    return View(vm);
                }
                hiddenArea.Area = polygon;

                await _dbContext.SaveChangesAsync();

                SetAlert("Hidden area updated successfully.", "success");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                SetAlert("Failed to update hidden area.", "error");
                HandleError(ex);
                return View(vm);
            }
        }


        // GET: User/HiddenAreas/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
                return NotFound();

            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.Id == id && m.User != null && m.User.UserName == userId);

            if (hiddenArea == null)
                return NotFound();

            return View(hiddenArea);
        }

        // POST: User/HiddenAreas/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var userId = User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var hiddenArea = await _dbContext.HiddenAreas
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.Id == id && m.User != null && m.User.UserName == userId);

            if (hiddenArea != null)
            {
                _dbContext.HiddenAreas.Remove(hiddenArea);
                await _dbContext.SaveChangesAsync();
                SetAlert("Hidden area deleted successfully.");
            }

            return RedirectToAction(nameof(Index));
        }
    }
}