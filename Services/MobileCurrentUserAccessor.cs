using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// HTTP-context backed accessor resolving users via bearer tokens.
/// </summary>
public class MobileCurrentUserAccessor : IMobileCurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ApplicationDbContext _dbContext;
    private ApplicationUser? _user;
    private bool _resolved;

    public MobileCurrentUserAccessor(IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbContext = dbContext;
    }

    public async Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved) return _user;
        _resolved = true;

        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;

        var authorization = context.Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)) return null;

        var token = authorization.Split(' ').LastOrDefault();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var apiToken = await _dbContext.ApiTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Token == token, cancellationToken);

        if (apiToken?.UserId == null) return null;

        _user = await _dbContext.Users
            .Include(u => u.ApiTokens)
            .FirstOrDefaultAsync(u => u.Id == apiToken.UserId, cancellationToken);

        return _user;
    }

    public void Reset()
    {
        _resolved = false;
        _user = null;
    }
}
