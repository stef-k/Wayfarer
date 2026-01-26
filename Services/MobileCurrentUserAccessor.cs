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

            var authorization = context.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(authorization)) return null;

            var token = authorization.Split(' ').LastOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Token is missing or empty.");
                return null;
            }

            // Hash the incoming token for comparison with stored hashes
            var tokenHash = ApiTokenService.HashToken(token);

            // Check for hashed token first, then fall back to plain text (third-party tokens)
            var apiToken = await _dbContext.ApiTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash || t.Token == token, cancellationToken);

            // Security: Log minimal token info for identification without exposing full secret
            var tokenInfo = GetSecureTokenInfo(token);

            if (apiToken?.UserId == null)
            {
                _logger.LogWarning("Token does not match any user. Token info: {TokenInfo}", tokenInfo);
                return null;
            }

            _user = await _dbContext.Users
                .Include(u => u.ApiTokens)
                .FirstOrDefaultAsync(u => u.Id == apiToken.UserId, cancellationToken);

            if (_user != null)
            {
                _logger.LogDebug("Mobile API request authenticated. UserId: {UserId}", _user.Id);
            }

            return _user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve user from token.");
            return null;
        }
    }

    public void Reset()
    {
        _resolved = false;
        _user = null;
    }

    /// <summary>
    /// Gets a secure representation of a token for logging purposes.
    /// Shows format type and partial identifier without exposing the full secret.
    /// </summary>
    /// <param name="token">The token to get info for.</param>
    /// <returns>A safe string representation for logging.</returns>
    private static string GetSecureTokenInfo(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return "empty";
        if (token.StartsWith("wf_") && token.Length >= 7)
            return $"{token.Substring(0, 7)}... (len={token.Length})";
        return $"old-format (len={token.Length})";
    }
}
