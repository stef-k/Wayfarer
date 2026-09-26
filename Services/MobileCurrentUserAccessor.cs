using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// HTTP-context backed accessor resolving users via bearer tokens.
/// </summary>
public class MobileCurrentUserAccessor : IMobileCurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<MobileCurrentUserAccessor> _logger;
    private ApplicationUser? _user;
    private bool _resolved;

    public MobileCurrentUserAccessor(
        IHttpContextAccessor httpContextAccessor,
        ApplicationDbContext dbContext,
        ILogger<MobileCurrentUserAccessor> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved) return _user;
        _resolved = true;

        try
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null) return null;

            _user = await IncomingApiTokenResolver.ForRequest(context, _dbContext)
                .ResolveAsync(context, cancellationToken);
            return _user;
        }
        catch (ApiTokenAdmissionException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve user from token.");
            return null;
        }
    }

    public void Reset()
    {
        if (_httpContextAccessor.HttpContext is { } context)
            IncomingApiTokenResolver.ForRequest(context, _dbContext).Reset();
        _resolved = false;
        _user = null;
    }

}
