using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class ApiTokenController : BaseController
    {
        private readonly ApiTokenService _apiTokenService;

        public ApiTokenController(ILogger<ApiTokenController> logger, ApplicationDbContext dbContext, ApiTokenService apiTokenService)
            : base(logger, dbContext)
        {
            _apiTokenService = apiTokenService;
        }

        /// <summary>
        /// Displays the current API tokens for a user.
        /// </summary>
        /// <param name="id">The ID of the user for whom the tokens will be displayed.</param>
        public async Task<IActionResult> Index(string id)
        {
            ApplicationUser? user = await _dbContext.Users.FindAsync(id);
            if (user == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            List<ApiToken> tokens = await _apiTokenService.GetTokensForUserAsync(id);

            ApiTokenViewModel viewModel = new ApiTokenViewModel
            {
                UserId = id,
                UserName = user.UserName,
                Tokens = tokens
            };
            SetPageTitle("API Token Management");
            return View(viewModel);
        }

        /// <summary>
        /// Generates a new API token for the user.
        /// </summary>
        /// <param name="id">The ID of the user for whom the token will be generated.</param>
        /// <param name="name">The name or purpose of the token.</param>
        [HttpPost]
        public async Task<IActionResult> Create(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                SetAlert("User ID and token name are required.", "danger");
                return RedirectToAction("Index", new { id });
            }

            try
            {
                // check if token exists for current user before creating it
                bool exists = await _dbContext.Users.Where(u => u.Id == id).SelectMany(u => u.ApiTokens).AnyAsync(t => t.Name == name);

                if (exists)
                {
                    SetAlert("This API Token name exists, please regenerate it or create an API Token with different name!", "warning");
                    return RedirectToAction("Index", new { id });
                }

                ApiToken apiToken = await _apiTokenService.CreateApiTokenAsync(id, name);
                SetAlert($"New token '{name}' created for {apiToken.User.UserName}.", "success");
            }
            catch (ArgumentException ex)
            {
                SetAlert(ex.Message, "danger");
            }

            return RedirectToAction("Index", new { id });
        }

        /// <summary>
        /// Regenerates an existing API token for a user.
        /// </summary>
        /// <param name="id">The ID of the user for whom the token will be regenerated.</param>
        /// <param name="name">The name of the token to regenerate.</param>
        public async Task<IActionResult> Regenerate(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                SetAlert("User ID and token name are required.", "danger");
                return RedirectToAction("Index", new { id }); ;
            }

            try
            {
                ApiToken apiToken = await _apiTokenService.RegenerateTokenAsync(id, name);
                ApplicationUser? user = await _dbContext.ApplicationUsers.FindAsync(id);
                SetAlert($"Token '{name}' has been regenerated for {user.UserName}.", "success");
            }
            catch (ArgumentException ex)
            {
                SetAlert(ex.Message, "danger");
            }

            return RedirectToAction("Index", new { id });
        }

        /// <summary>
        /// Deletes a specific API token for the user.
        /// </summary>
        /// <param name="id">The ID of the user for whom the token will be deleted.</param>
        /// <param name="tokenId">The ID of the token to delete.</param>
        public async Task<IActionResult> Delete(string id, int tokenId)
        {
            if (string.IsNullOrEmpty(id) || tokenId == 0)
            {
                SetAlert("Invalid request. Token and User ID are required.", "danger");
                return RedirectToAction("Index", new { id });
            }

            ApiToken? token = await _dbContext.ApiTokens.FindAsync(tokenId);

            if (token == null || token.UserId != id)
            {
                SetAlert("Token not found or does not belong to the specified user.", "danger");
                return RedirectToAction("Index", new { id });
            }

            if (token.Name.Equals("Wayfarer Incoming Location Data API Token"))
            {
                SetAlert("This is the default API Token needed for incoming location data and therefore cannot be deleted!", "warning");
                return RedirectToAction("Index", new { id });
            }

            _dbContext.ApiTokens.Remove(token);
            await _dbContext.SaveChangesAsync();

            SetAlert($"Token '{token.Name}' has been deleted.", "success");
            return RedirectToAction("Index", new { id });
        }
    }
}