using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class ApiTokenController : BaseController
    {
        private readonly ApiTokenService _apiTokenService;  // Directly use ApiTokenService

        public ApiTokenController(ApiTokenService apiTokenService, ILogger<BaseController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext) // Pass dependencies to the base controller
        {
            _apiTokenService = apiTokenService;  // Initialize ApiTokenService directly
        }

        // GET: ApiToken/Index
        public async Task<IActionResult> Index()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier); // Get current logged-in user ID

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            List<ApiToken> tokens = await _apiTokenService.GetTokensForUserAsync(userId); // Fetch tokens for the logged-in user
            ApiTokenViewModel viewModel = new ApiTokenViewModel
            {
                UserName = User.Identity?.Name ?? string.Empty,
                UserId = userId,
                Tokens = tokens
            };

            SetPageTitle("API Token Management");
            return View(viewModel);
        }

        // POST: ApiToken/Create
        [HttpPost]
        public async Task<IActionResult> Create(string name)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            // check if token exists for current user before creating it
            bool exists = await _dbContext.ApiTokens.AnyAsync(t => t.UserId == userId && t.Name == name);

            if (exists)
            {
                SetAlert("This API Token name exists, please regenerate it or create an API Token with different name!", "warning");
                return RedirectToAction("Index");
            }

            (ApiToken _, string plainToken) = await _apiTokenService.CreateApiTokenAsync(userId, name);

            // Store plain token in TempData for one-time display
            TempData["NewToken"] = plainToken;
            TempData["NewTokenName"] = name;

            SetAlert("API token created successfully! Copy it now - it won't be shown again.", "success");
            return RedirectToAction("Index");
        }
        
        /// <summary>
        /// Store a third-party token for the current user
        /// </summary>
        /// <param name="name">Name of third party service</param>
        /// <param name="token">Token</param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> StoreThirdPartyToken(string thirdPartyServiceName, string thirdPartyToken)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            // Check if token exists for current user before creating it
            bool exists = await _dbContext.ApiTokens.AnyAsync(t =>
                t.UserId == userId &&
                (t.Name.Trim().ToLower() == thirdPartyServiceName.Trim().ToLower() ||
                 t.Name.Trim().ToLower().Contains(thirdPartyServiceName.Trim().ToLower()))
            );

            if (exists)
            {
                SetAlert("This API Token name exists, please regenerate it or create an API Token with a different name!", "warning");
                return RedirectToAction("Index");
            }

            // Store the new token
            ApiToken newToken = await _apiTokenService.StoreThirdPartyToken(userId, thirdPartyServiceName.Trim(), thirdPartyToken.Trim());

            SetAlert("API token created successfully!", "success");  // Example of using SetAlert from BaseController
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Regenerates the API token for the specified token name.
        /// Returns JSON for AJAX requests with the new token (shown once only).
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Regenerate(string name)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { success = false, message = "User not authenticated." });
            }

            (ApiToken _, string plainToken) = await _apiTokenService.RegenerateTokenAsync(userId, name);

            // Return JSON for AJAX requests - this is the only time the plain token is shown
            return Json(new {
                success = true,
                token = plainToken,
                message = "API token regenerated successfully! Copy it now - it won't be shown again. Update your mobile app and any third-party clients with the new token."
            });
        }

        // GET: ApiToken/Delete/{tokenId}
        [HttpGet("Delete/{tokenId}")]
        public async Task<IActionResult> Delete(int tokenId)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            List<ApiToken> token = await _apiTokenService.GetTokensForUserAsync(userId);

            ApiToken? tokenToDelete = token.FirstOrDefault(t => t.Id == tokenId);
            if (tokenToDelete == null)
            {
                return NotFound(); // Token not found
            }

            ApiTokenDeleteViewModel viewModel = new ApiTokenDeleteViewModel
            {
                TokenId = tokenToDelete.Id,
                TokenName = tokenToDelete.Name,
            };

            if (tokenToDelete.Name.Equals("Wayfarer Incoming Location Data API Token"))
            {
                SetAlert("This is the default API Token needed for incoming location data and therefore cannot be deleted!", "warning");
                return RedirectToAction("Index");
            }

            return View(viewModel); // Pass token details to the confirmation view
        }

        // POST: ApiToken/Delete
        [HttpPost("Delete")]
        public async Task<IActionResult> DeleteConfirmed(int tokenId)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier); // Get current user's ID

            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            try
            {
                await _apiTokenService.DeleteTokenForUserAsync(userId, tokenId); // Call the service to delete the token
                SetAlert("API token deleted successfully!", "success"); // Show success alert
                return RedirectToAction("Index"); // Redirect to the list of API tokens
            }
            catch (ArgumentException ex)
            {
                SetAlert(ex.Message, "danger"); // Show error alert if something goes wrong
                return RedirectToAction("Index");
            }
        }
    }
}