using Microsoft.AspNetCore.Mvc;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Common bearer, snapshot and refusal handling for reviewed Group workflows.</summary>
[ApiController]
[Area("Api")]
public abstract class MobileGroupWorkflowController(ApplicationDbContext db) : ControllerBase
{
    /// <summary>The request context shared with the Group services and incoming token resolver.</summary>
    protected ApplicationDbContext Db { get; } = db;

    /// <summary>Resolves the bearer in the same snapshot as every protected read.</summary>
    protected async Task<IActionResult> ReadAsync(Func<string, Task<IActionResult>> read, CancellationToken ct)
    {
        BrowserResponsePolicyMiddleware.RequirePrivateResponse(HttpContext);
        await using var snapshot = await GroupRead.BeginAsync(Db, ct);
        return await WithBearerAsync((actor, _) => read(actor), ct);
    }

    /// <summary>Admits the incoming request and hands the service its post-lock credential recheck.</summary>
    protected async Task<IActionResult> WithBearerAsync(
        Func<string, Func<CancellationToken, Task<bool>>, Task<IActionResult>> action, CancellationToken ct)
    {
        var user = await MobileBearer.ResolveAsync(HttpContext, Db, ct);
        if (user is null) return UnauthorizedBearer();

        try
        {
            var resolver = IncomingApiTokenResolver.ForRequest(HttpContext, Db);
            return await action(user.Id, token => resolver.RevalidateMutationAsync(HttpContext, user.Id, token));
        }
        catch (GroupRefusal refusal)
        {
            if (refusal.Status == 401) return UnauthorizedBearer();
            var code = refusal.Code is "invitation_unaddressed" or "invitation_group_unavailable"
                ? "invitation_unavailable" : refusal.Code;
            return StatusCode(refusal.Status, new { code });
        }
    }

    /// <summary>The established mobile unauthorized envelope.</summary>
    private IActionResult UnauthorizedBearer() => Unauthorized(new { message = "Invalid or missing API token." });
}
