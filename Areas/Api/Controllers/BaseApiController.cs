using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

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

                ApiToken? apiToken = _dbContext.ApiTokens.FirstOrDefault(t => t.Token == token);

                if (apiToken?.UserId == null)
                {
                    _logger.LogWarning("Token does not match any user.");
                    return null;
                }

                return _dbContext.Users
                    .Include(u => u.ApiTokens)
                    .FirstOrDefault(u => u.Id == apiToken.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve user from token.");
                return null;
            }
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
        /// <param name="value"></param>
        /// <returns></returns>
        protected Point SanitizePoint(Point point)
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