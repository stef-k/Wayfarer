using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Read-only account proof using the existing incoming token authority, without cookie fallback.</summary>
/// <param name="dbContext">The request's application store used by the shared token resolver.</param>
[ApiController]
[Area("Api")]
[Route("api/mobile/me")]
public sealed class MobileAccountController(ApplicationDbContext dbContext) : ControllerBase
{
    /// <summary>Returns only the active bearer owner's stable account ID; store failures remain server errors.</summary>
    [HttpGet]
    [Produces("application/json")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // Apply after error middleware too, which can clear headers when a lookup fails.
        Response.OnStarting(() =>
        {
            Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });

        // Validate this additive route without tightening the loose parser used by existing clients.
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter)
            || authorization.Parameter.Any(char.IsWhiteSpace))
            return Unauthorized(new { message = "Invalid or missing API token." });

        // The mobile accessor catches store failures as null; proof must preserve failures as 500, not 401.
        var user = await IncomingApiTokenResolver.ForRequest(HttpContext, dbContext)
            .ResolveTokenAsync(authorization.Parameter, RateLimitHelper.GetClientIpAddress(HttpContext), cancellationToken);
        if (user is not { IsActive: true })
            return Unauthorized(new { message = "Invalid or missing API token." });

        return Ok(new { userId = user.Id });
    }
}
