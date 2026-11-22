using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class SettingsController(ApplicationDbContext dbContext,
        ILogger<SettingsController> logger,
        UserManager<ApplicationUser> userManager,
        IApplicationSettingsService settingsService) : BaseController(logger, dbContext)
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;
        private readonly IApplicationSettingsService _settingsService = settingsService;

        /// <summary>Displays the owner’s settings and the server’s logging thresholds.</summary>
        public async Task<IActionResult> Index()
        {
            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "error", "Settings.UserUnauthenticated");
                return RedirectToAction("Login", "Account");
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(userId);

            if (user == null)
            {
                SetAlert("User not found.", "error", "Settings.UserMissing");
                return RedirectToAction("Login", "Account");
            }

            var settings = _settingsService.GetSettings();
            ViewData["LocationTimeThresholdMinutes"] = settings.LocationTimeThresholdMinutes;
            ViewData["LocationDistanceThresholdMeters"] = settings.LocationDistanceThresholdMeters;
            ViewData["LocationAccuracyThresholdMeters"] = settings.LocationAccuracyThresholdMeters;

            return View(user);
        }
    }
}