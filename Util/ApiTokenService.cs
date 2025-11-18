using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Wayfarer.Models;

namespace Wayfarer.Util
{
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
        /// Creates a new API Token for the specified user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="name">Name of the service/purpose the token will be used</param>
        /// <returns>Generated API Token</returns>
        /// <exception cref="ArgumentException">If user is not found in DB</exception>
        public async Task<ApiToken> CreateApiTokenAsync(string userId, string name)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException("User not found.");
            }

            string token = GenerateToken();
            ApiToken apiToken = new ApiToken
            {
                UserId = userId,
                User = user,
                Name = name,
                Token = token,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ApiTokens.Add(apiToken);
            await _dbContext.SaveChangesAsync();

            return apiToken;
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
        /// Validates the API Token for the specified user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="token">The API token to validate</param>
        /// <returns>True if API token is found and valid for current User</returns>
        public async Task<bool> ValidateApiTokenAsync(string userId, string token)
        {
            ApiToken? apiToken = await _dbContext.ApiTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.Token == token);

            return apiToken != null;
        }

        /// <summary>
        /// Regenerates the API Token for the specified user and token name
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public async Task<ApiToken> RegenerateTokenAsync(string userId, string name)
        {
            ApiToken? apiToken = await _dbContext.ApiTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.Name == name);

            if (apiToken == null)
            {
                throw new ArgumentException($"Token with Name '{name}' does not exist for the user.");
            }

            // Generate a new token
            apiToken.Token = GenerateToken();
            apiToken.CreatedAt = DateTime.UtcNow;

            _dbContext.ApiTokens.Update(apiToken);
            await _dbContext.SaveChangesAsync();

            return apiToken;
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