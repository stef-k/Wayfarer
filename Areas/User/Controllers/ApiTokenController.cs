using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.ConnectionModels;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Personal single-credential connections: cookie Identity, active account and direct committed reveal.</summary>
[Area("User")]
[Authorize(AuthenticationSchemes = "Identity.Application", Roles = "User")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ApiTokenController(ApplicationDbContext db, ApiTokenService tokens) : Controller
{
    /// <summary>Renders only safe status; JSON negotiation refreshes uncertain browser state through the same GET.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var owner = await CurrentUserAsync(cancellationToken);
        if (owner == null) return StatusCode(403);
        var status = await tokens.GetConnectionTokenStatusAsync(owner.Id, cancellationToken);
        if (Request.Headers.Accept.Contains("application/json")) return Json(status);
        ViewData["Title"] = "Connect apps to Wayfarer";
        return View(new ConnectionTokenViewModel(owner.UserName ?? string.Empty, status, Request.IsHttps));
    }

    /// <summary>Explicitly creates the absent canonical verifier; no caller-selected owner or name is accepted.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var owner = await CurrentUserAsync(cancellationToken);
        if (owner == null) return StatusCode(403);
        if (!Request.IsHttps) return BadRequest(new { message = "Use HTTPS. The instance or proxy configuration needs correction." });
        return await IssueAsync(() => tokens.CreateConnectionTokenAsync(owner.Id, cancellationToken), 201);
    }

    /// <summary>Requires confirmation and exact observed state before atomically replacing the owner's credential.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Replace([FromBody] ReplaceConnectionTokenInput input, CancellationToken cancellationToken)
    {
        var owner = await CurrentUserAsync(cancellationToken);
        if (owner == null) return StatusCode(403);
        if (!Request.IsHttps) return BadRequest(new { message = "Use HTTPS. The instance or proxy configuration needs correction." });
        if (!ModelState.IsValid || input == null || !input.Confirmed || input.IssuedAt == null)
            return BadRequest(new { message = "Confirm replacement using the current connection token status." });
        return await IssueAsync(() => tokens.ReplaceConnectionTokenAsync(owner.Id, input.TokenId,
            input.IssuedAt.Value, cancellationToken), 200);
    }

    /// <summary>Rejects stale cookie owners that are missing or inactive in the current database.</summary>
    private Task<ApplicationUser?> CurrentUserAsync(CancellationToken cancellationToken)
    {
        var ownerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == ownerId && user.IsActive, cancellationToken);
    }

    /// <summary>Reveals only a committed success and returns bounded, secret-free expected failure messages.</summary>
    private async Task<IActionResult> IssueAsync(Func<Task<ConnectionTokenIssue?>> issue, int successStatus)
    {
        try
        {
            var result = await issue();
            return result == null
                ? Conflict(new { message = "Connection token status changed. Refresh before trying again." })
                : new JsonResult(result) { StatusCode = successStatus };
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            return StatusCode(503, new { message = "The request could not be completed. Refresh status before explicitly trying again." });
        }
    }
}
