using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Base class for mobile API endpoints that authenticate via API tokens.
/// </summary>
[ApiController]
[Area("Api")]
public abstract class MobileApiController : ControllerBase
{
    protected readonly ApplicationDbContext DbContext;
    protected readonly ILogger<BaseApiController> Logger;
    private readonly IMobileCurrentUserAccessor _userAccessor;
    private ApplicationUser? _cachedUser;
    private bool _resolved;

    protected MobileApiController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IMobileCurrentUserAccessor userAccessor)
    {
        DbContext = dbContext;
        Logger = logger;
        _userAccessor = userAccessor;
    }

    protected async Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved) return _cachedUser;
        _cachedUser = await _userAccessor.GetCurrentUserAsync(cancellationToken);
        _resolved = true;
        return _cachedUser;
    }

    protected async Task<(ApplicationUser? user, IActionResult? error)> EnsureAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return (null, Unauthorized(new { message = "Invalid or missing API token." }));
        }

        if (!user.IsActive)
        {
            return (null, Forbid());
        }

        return (user, null);
    }

    protected void ResetUserCache()
    {
        _userAccessor.Reset();
        _cachedUser = null;
        _resolved = false;
    }
}
