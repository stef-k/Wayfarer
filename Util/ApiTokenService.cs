using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Wayfarer.Models;

namespace Wayfarer.Util
{
    /// <summary>
    /// Service for managing API tokens with secure hashing for Wayfarer-generated tokens.
    /// </summary>
    public class ApiTokenService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;

        public ApiTokenService(ApplicationDbContext dbContext, UserManager<ApplicationUser> userManager)
        {
            _dbContext = dbContext;
            _userManager = userManager;
        }

        /// <summary>
        /// Computes SHA-256 hash of a token for secure storage and comparison.
        /// </summary>
        /// <param name="token">The plain text token to hash</param>
        /// <returns>Lowercase hexadecimal hash string</returns>
        public static string HashToken(string token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(token);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Generates a random 32-byte token and encodes it as a Base64 string
        /// </summary>
        /// <returns>Generated API Token</returns>
        public string GenerateToken()
        {
            byte[] tokenData = new byte[32]; // 32-byte token (256 bits)
            RandomNumberGenerator.Fill(tokenData); // Fills the array with cryptographically strong random bytes
            return ToCustomUrlSafeBase64(tokenData); // Encode as a Base64 string
        }

        /// <summary>
        /// Creates a new API Token for the specified user.
        /// The token is stored as a hash for security. The plain token is returned
        /// separately and should be shown to the user only once.
        /// </summary>
        /// <param name="userId">The user ID to create the token for</param>
        /// <param name="name">Name of the service/purpose the token will be used</param>
        /// <returns>Tuple of (ApiToken entity, plain text token for one-time display)</returns>
        /// <exception cref="ArgumentException">If user is not found in DB</exception>
        public async Task<(ApiToken apiToken, string plainToken)> CreateApiTokenAsync(string userId, string name)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException("User not found.");
            }

            string plainToken = GenerateToken();
            string tokenHash = HashToken(plainToken);

            ApiToken apiToken = new ApiToken
            {
                UserId = userId,
                User = user,
                Name = name,
                Token = null, // Don't store plain token for Wayfarer-generated tokens
                TokenHash = tokenHash,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ApiTokens.Add(apiToken);
            await _dbContext.SaveChangesAsync();

            return (apiToken, plainToken);
        }

        public async Task<ApiToken> StoreThirdPartyToken(string userId, string thirdPartyServiceName, string thirdPartyToken)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException("User not found.");
            }

            ApiToken apiToken = new ApiToken
            {
                UserId = userId,
                User = user,
                Name = thirdPartyServiceName,
                Token = thirdPartyToken,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ApiTokens.Add(apiToken);
            await _dbContext.SaveChangesAsync();
            return apiToken;
        }

        /// <summary>
        /// Validates the API Token for the specified user.
        /// Supports both hashed (Wayfarer) and plain text (third-party) tokens.
        /// </summary>
        /// <param name="userId">The user ID to validate against</param>
        /// <param name="token">The API token to validate</param>
        /// <returns>True if API token is found and valid for current User</returns>
        public async Task<bool> ValidateApiTokenAsync(string userId, string token)
        {
            string tokenHash = HashToken(token);

            // Check for hashed token first, then fall back to plain text (third-party tokens)
            ApiToken? apiToken = await _dbContext.ApiTokens
                .FirstOrDefaultAsync(t => t.UserId == userId &&
                    (t.TokenHash == tokenHash || t.Token == token));

            return apiToken != null;
        }

        /// <summary>
        /// Regenerates the API Token for the specified user and token name.
        /// The new token is stored as a hash. The plain token is returned for one-time display.
        /// </summary>
        /// <param name="userId">The user ID</param>
        /// <param name="name">The token name to regenerate</param>
        /// <returns>Tuple of (ApiToken entity, plain text token for one-time display)</returns>
        /// <exception cref="ArgumentException">If token not found</exception>
        public async Task<(ApiToken apiToken, string plainToken)> RegenerateTokenAsync(string userId, string name)
        {
            ApiToken? apiToken = await _dbContext.ApiTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.Name == name);

            if (apiToken == null)
            {
                throw new ArgumentException($"Token with Name '{name}' does not exist for the user.");
            }

            // Generate a new token and store only the hash
            string plainToken = GenerateToken();
            apiToken.Token = null;
            apiToken.TokenHash = HashToken(plainToken);
            apiToken.CreatedAt = DateTime.UtcNow;

            _dbContext.ApiTokens.Update(apiToken);
            await _dbContext.SaveChangesAsync();

            return (apiToken, plainToken);
        }

        /// <summary>
        /// Retrieves all API tokens for a specified user.
        /// </summary>
        /// <param name="userId">The ID of the user to retrieve tokens for.</param>
        /// <returns>A list of API tokens associated with the user.</returns>
        public async Task<List<ApiToken>> GetTokensForUserAsync(string userId)
        {
            List<ApiToken> tokens = await _dbContext.ApiTokens
                .Where(t => t.UserId == userId)
                .ToListAsync();

            return tokens;
        }

        /// <summary>
        /// Deletes an API token for a specific user.
        /// </summary>
        /// <param name="userId">The ID of the user whose token will be deleted.</param>
        /// <param name="tokenId">The ID of the token to delete.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task DeleteTokenForUserAsync(string userId, int tokenId)
        {
            // Find the token by ID and ensure it belongs to the specified user
            ApiToken? token = await _dbContext.ApiTokens
                .FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId);

            if (token == null)
            {
                throw new ArgumentException("Token not found or does not belong to the specified user.");
            }

            // Remove the token from the database
            _dbContext.ApiTokens.Remove(token);
            await _dbContext.SaveChangesAsync();
        }

        /// <summary>
        /// Converts token bytes to RFC 4648 URL-safe Base64 with Wayfarer prefix.
        /// Format: wf_[base64] where base64 uses - instead of + and _ instead of /
        /// Example: wf_YQz2_nK8mJ-3oPx_zA1B-vN7rD4tU6wL5eF9hG
        /// </summary>
        private static string ToCustomUrlSafeBase64(byte[] tokenData)
        {
            // Convert to Base64 and replace non-URL-safe characters per RFC 4648
            string base64 = Convert.ToBase64String(tokenData)
                .Replace('+', '-')              // Replace '+' with '-'
                .Replace('/', '_')              // Replace '/' with '_' (underscore, not dash)
                .Replace("=", string.Empty);    // Remove padding '='

            // Add Wayfarer prefix for easy identification
            return $"wf_{base64}";
        }
    }
}