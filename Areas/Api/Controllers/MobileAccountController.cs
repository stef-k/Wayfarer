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

        var user = await MobileBearer.ResolveAsync(HttpContext, dbContext, cancellationToken);
        if (user is not { IsActive: true })
            return Unauthorized(new { message = "Invalid or missing API token." });

        return Ok(new { userId = user.Id });
    }
}
