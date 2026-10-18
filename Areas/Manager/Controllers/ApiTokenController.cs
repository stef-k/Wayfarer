using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Areas.Manager.Controllers
{
    [Area("Manager")]
    [Authorize(Roles = "Manager")]
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
            // Drop legacy unused reveals while preserving normal alerts.
            TempData.Remove("NewToken");
            TempData.Remove("NewTokenName");
            ApplicationUser? user = await _dbContext.Users.FindAsync(id);
            if (user == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            // Target eligibility precedes token metadata retrieval.
            if (!await ManagerTargetUsers.IsEligibleAsync(_dbContext, user.Id))
            {
                return Forbid();
            }

            List<ApiToken> tokens = await _apiTokenService.GetTokensForUserAsync(id);

            ApiTokenViewModel viewModel = new ApiTokenViewModel
            {
                UserId = id,
                UserName = user.UserName ?? string.Empty,
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
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                SetAlert("User ID and token name are required.", "danger");
                return RedirectToAction("Index", new { id });
            }

            var cancellationToken = HttpContext.RequestAborted;
            await using var transaction = await BeginTokenMutationAsync(cancellationToken);
            if (!await LockAndAuthorizeTargetAsync(id, cancellationToken))
            {
                return Forbid();
            }

            try
            {
                // check if token exists for current user before creating it
                bool exists = await _dbContext.Users.Where(u => u.Id == id).SelectMany(u => u.ApiTokens)
                    .AnyAsync(t => t.Name == name, cancellationToken);

                if (exists)
                {
                    SetAlert("This API Token name exists, please regenerate it or create an API Token with different name!", "warning");
                    return RedirectToAction("Index", new { id });
                }

                (ApiToken apiToken, _) = await _apiTokenService.CreateApiTokenAsync(id, name);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                SetAlert($"New token '{name}' created for {apiToken.User.UserName}. No plaintext is retained or displayed here.", "success");
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
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Regenerate(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                SetAlert("User ID and token name are required.", "danger");
                return RedirectToAction("Index", new { id });
            }

            var cancellationToken = HttpContext.RequestAborted;
            await using var transaction = await BeginTokenMutationAsync(cancellationToken);
            if (!await LockAndAuthorizeTargetAsync(id, cancellationToken))
            {
                return Forbid();
            }

            try
            {
                (ApiToken apiToken, _) = await _apiTokenService.RegenerateTokenAsync(id, name);
                ApplicationUser? user = await _dbContext.ApplicationUsers.FindAsync(id);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                SetAlert($"Token '{name}' has been regenerated for {user?.UserName ?? id}. No plaintext is retained or displayed here.", "success");
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
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id, int tokenId)
        {
            if (string.IsNullOrEmpty(id) || tokenId == 0)
            {
                SetAlert("Invalid request. Token and User ID are required.", "danger");
                return RedirectToAction("Index", new { id });
            }

            var cancellationToken = HttpContext.RequestAborted;
            await using var transaction = await BeginTokenMutationAsync(cancellationToken);
            if (!await LockAndAuthorizeTargetAsync(id, cancellationToken))
            {
                return Forbid();
            }

            ApiToken? token = await _dbContext.ApiTokens.FindAsync([tokenId], cancellationToken);
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
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);

            SetAlert($"Token '{token.Name}' has been deleted.", "success");
            return RedirectToAction("Index", new { id });
        }

        /// <summary>
        /// Owns the entire named-token mutation, including service saves on this scoped DbContext.
        /// Explicit PostgreSQL Read Committed gives the role SELECT a fresh snapshot after any lock wait.
        /// Disposal rolls back denial, early return, service failure or cancellation before commit.
        /// Nonrelational unit-test stores have no transactions; other relational test stores use their default.
        /// </summary>
        private async Task<IDbContextTransaction?> BeginTokenMutationAsync(CancellationToken cancellationToken)
        {
            if (!_dbContext.Database.IsRelational()) return null;
            return _dbContext.Database.IsNpgsql()
                ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        /// <summary>
        /// Locks the PostgreSQL Identity parent before querying complete persisted roles. Identity role
        /// writes update this row, so promotion cannot cross authorization and token commit. Missing
        /// targets continue to the existing action-specific handling. Unit stores still check roles.
        /// </summary>
        private async Task<bool> LockAndAuthorizeTargetAsync(string id, CancellationToken cancellationToken)
        {
            if (_dbContext.Database.IsNpgsql())
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {id} FOR UPDATE""", cancellationToken);

            return !await _dbContext.Users.AnyAsync(user => user.Id == id, cancellationToken)
                || await ManagerTargetUsers.Eligible(_dbContext).AnyAsync(user => user.Id == id, cancellationToken);
        }
    }
}
