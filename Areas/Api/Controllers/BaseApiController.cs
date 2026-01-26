using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers
{
    [ApiController]
    [Area("Api")]
    public abstract class BaseApiController : ControllerBase
    {
        protected readonly ApplicationDbContext _dbContext;
        protected readonly ILogger<BaseApiController> _logger;

        protected BaseApiController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// Gets the authenticated user from the token.
        /// Supports both hashed (Wayfarer) and plain text (third-party) tokens.
        /// </summary>
        /// <returns>The ApplicationUser or null if not found.</returns>
        protected ApplicationUser? GetUserFromToken()
        {
            try
            {
                string? token = Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").Last();

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("Token is missing or empty.");
                    return null;
                }

                // Hash the incoming token for comparison with stored hashes
                string tokenHash = ApiTokenService.HashToken(token);

                // Check for hashed token first, then fall back to plain text (third-party tokens)
                ApiToken? apiToken = _dbContext.ApiTokens
                    .FirstOrDefault(t => t.TokenHash == tokenHash || t.Token == token);

                // Security: Log minimal token info for identification without exposing full secret
                string tokenInfo = GetSecureTokenInfo(token);

                if (apiToken?.UserId == null)
                {
                    _logger.LogWarning("Token does not match any user. Token info: {TokenInfo}", tokenInfo);
                    return null;
                }

                ApplicationUser? user = _dbContext.Users
                    .Include(u => u.ApiTokens)
                    .FirstOrDefault(u => u.Id == apiToken.UserId);

                if (user != null)
                {
                    _logger.LogDebug("API request authenticated. UserId: {UserId}", user.Id);
                }

                return user;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve user from token.");
                return null;
            }
        }

        /// <summary>
        /// Gets the authenticated user from cookie identity when present, otherwise falls back to API token.
        /// </summary>
        /// <returns>The ApplicationUser or null if not found.</returns>
        protected ApplicationUser? GetUserFromTokenOrCookie()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId))
                {
                    return null;
                }

                return _dbContext.Users
                    .Include(u => u.ApiTokens)
                    .FirstOrDefault(u => u.Id == userId);
            }

            return GetUserFromToken();
        }

        /// <summary>
        /// Gets the role of the authenticated user.
        /// </summary>
        /// <returns>The role or null if no user is found.</returns>
        protected string? GetUserRole()
        {
            ApplicationUser? user = GetUserFromToken();
            if (user == null)
            {
                return null;
            }

            Microsoft.AspNetCore.Identity.IdentityUserClaim<string>? roleClaim = _dbContext.UserClaims
                .FirstOrDefault(c => c.UserId == user.Id && c.ClaimType == ClaimTypes.Role);

            return roleClaim?.ClaimValue;
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

        /// <summary>
        /// Helper method to sanitize floating point values for use in JSON serialization
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        protected double? SanitizeFloat(double? value)
        {
            return value.HasValue && (double.IsInfinity(value.Value) || double.IsNaN(value.Value)) ? null : value;
        }

        /// <summary>
        /// Helper method to sanitize floating point values for use in JSON serialization
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        protected Point? SanitizePoint(Point? point)
        {
            if (point == null)
            {
                return null;
            }

            // Sanitize latitude (Y) and longitude (X) values
            double? sanitizedLatitude = SanitizeFloat(point.Y);
            double? sanitizedLongitude = SanitizeFloat(point.X);

            // Return a new Point with sanitized coordinates
            return new Point(sanitizedLongitude ?? point.X, sanitizedLatitude ?? point.Y);
        }
    }
}
