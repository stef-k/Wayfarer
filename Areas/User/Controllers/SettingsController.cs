using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class SettingsController(ApplicationDbContext dbContext,
        ILogger<SettingsController> logger,
        UserManager<ApplicationUser> userManager) : BaseController(logger, dbContext)
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;

        public async Task<IActionResult> Index()
        {
            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                return RedirectWithAlert("Login", "Account", "User not authenticated", "error", null, null);
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(userId);

            if (user == null)
            {
                return RedirectWithAlert("Login", "Account", "User not found", "error", null, null);
            }

            SetPageTitle("Settings Management");
            return View(user);
        }
    }
}