using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class TimelineController : BaseController
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public TimelineController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            UserManager<ApplicationUser> userManager)
            : base(logger, dbContext)
        {
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        }

        // Action to display the timeline for the logged-in user
        public async Task<IActionResult> Index()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
            ViewData["Username"] = currentUser.UserName;
            
            SetPageTitle("Private Timeline");
            return View();
        }

        // GET: Timeline/Settings
        public async Task<IActionResult> Settings()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            TimelineSettingsViewModel model = new TimelineSettingsViewModel
            {
                IsTimelinePublic = currentUser.IsTimelinePublic,
                PublicTimelineTimeThreshold = currentUser.PublicTimelineTimeThreshold
            };

            SetPageTitle("Timeline Settings");
            return View(model);
        }

        // POST: Timeline/Settings
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSettings(TimelineSettingsViewModel model)
        {
            if (model.PublicTimelineTimeThreshold != "custom")
            {
                model.CustomThreshold = null;  // Optionally clear the value
                ModelState.Remove("CustomThreshold");  // Skip validation for CustomThreshold
            }

            if (!ValidateModelState())
            {
                return View("Settings", model);
            }

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            try
            {
                currentUser.IsTimelinePublic = model.IsTimelinePublic;

                // If the value is "custom", use the custom input
                if (model.PublicTimelineTimeThreshold == "custom" && !string.IsNullOrEmpty(model.CustomThreshold))
                {
                    if (!TimespanHelper.IsValidThreshold(model.CustomThreshold))
                    {
                        ModelState.AddModelError("CustomThreshold", "Invalid time threshold format. Use values like 1d, 2.5w, 0.1h, etc.");
                        return View("Settings", model);
                    }
                    currentUser.PublicTimelineTimeThreshold = model.CustomThreshold; // Save custom input
                }
                else
                {
                    currentUser.PublicTimelineTimeThreshold = model.PublicTimelineTimeThreshold;
                }

                _dbContext.Users.Update(currentUser);
                await _dbContext.SaveChangesAsync();

                SetAlert("Timeline settings updated successfully.");
                LogAction("TimelineSettingsUpdate", $"User {currentUser.UserName} updated their timeline settings.");
                return RedirectToAction("Settings");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View("Settings", model);
            }
        }
    }
}