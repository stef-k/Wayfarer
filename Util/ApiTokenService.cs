using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Util
{
    /// <summary>
    /// Service for managing API tokens with secure hashing for Wayfarer-generated tokens.
    /// </summary>
    public class ApiTokenService
    {
        /// <summary>The fixed incoming credential owned by the personal Connect apps workflow.</summary>
        public const string ConnectionTokenName = "Wayfarer Incoming Location Data API Token";

        private readonly ApplicationDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHttpContextAccessor? _httpContextAccessor;

        /// <summary>Shares generation, persistence and incoming request authority with existing token management.</summary>
        public ApiTokenService(ApplicationDbContext dbContext, UserManager<ApplicationUser> userManager,
            IHttpContextAccessor? httpContextAccessor = null)
        {
            _httpContextAccessor = httpContextAccessor;
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

        /// <summary>Projects only the authenticated owner's canonical row identity and issuance state.</summary>
        public Task<ConnectionTokenStatus?> GetConnectionTokenStatusAsync(string userId,
            CancellationToken cancellationToken = default) => _dbContext.ApiTokens.AsNoTracking()
            .Where(token => token.UserId == userId && token.Name == ConnectionTokenName)
            .Select(token => new ConnectionTokenStatus(token.Id, token.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        /// <summary>Creates an absent canonical verifier; competing inserts are decided by the existing unique index.</summary>
        public async Task<ConnectionTokenIssue?> CreateConnectionTokenAsync(string userId,
            CancellationToken cancellationToken = default)
        {
            RequireImmediateCommit();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            if (await GetConnectionTokenStatusAsync(userId, cancellationToken) != null) return null;
            var owner = await _dbContext.Users.SingleAsync(user => user.Id == userId && user.IsActive, cancellationToken);
            var plaintext = GenerateToken();
            var credential = new ApiToken
            {
                UserId = owner.Id,
                User = owner,
                Name = ConnectionTokenName,
                Token = null,
                TokenHash = HashToken(plaintext),
                CreatedAt = AtPostgresPrecision(DateTime.UtcNow)
            };
            _dbContext.ApiTokens.Add(credential);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_ApiToken_Name_UserId" })
            {
                _dbContext.Entry(credential).State = EntityState.Detached;
                return null;
            }
            return new ConnectionTokenIssue(plaintext, credential.Id, credential.CreatedAt);
        }

        /// <summary>Replaces only the observed canonical row/version, using one conditional database update.</summary>
        public async Task<ConnectionTokenIssue?> ReplaceConnectionTokenAsync(string userId,
            int tokenId, DateTime issuedAt, CancellationToken cancellationToken = default)
        {
            RequireImmediateCommit();
            if (issuedAt.Kind != DateTimeKind.Utc || issuedAt.Ticks > DateTime.MaxValue.Ticks - 10) return null;
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var nextIssuedAt = AtPostgresPrecision(DateTime.UtcNow);
            if (nextIssuedAt <= issuedAt) nextIssuedAt = AtPostgresPrecision(issuedAt).AddTicks(10);
            var plaintext = GenerateToken();
            var hash = HashToken(plaintext);
            // PostgreSQL rechecks the full predicate after a competing writer commits.
            var changed = await _dbContext.ApiTokens.Where(token => token.Id == tokenId
                    && token.UserId == userId && token.Name == ConnectionTokenName && token.CreatedAt == issuedAt
                    && token.User.IsActive)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.Token, (string?)null)
                    .SetProperty(token => token.TokenHash, hash).SetProperty(token => token.CreatedAt, nextIssuedAt),
                    cancellationToken);
            if (changed != 1) return null;
            await transaction.CommitAsync(cancellationToken);
            return new ConnectionTokenIssue(plaintext, tokenId, nextIssuedAt);
        }

        /// <summary>Refuses caller-owned transactions so the one-time result cannot precede its actual commit.</summary>
        private void RequireImmediateCommit()
        {
            if (_dbContext.Database.CurrentTransaction != null || System.Transactions.Transaction.Current != null)
                throw new InvalidOperationException("Connection token issuance requires an immediate database commit.");
        }

        /// <summary>Matches PostgreSQL microseconds exactly in both persisted and returned timestamps.</summary>
        private static DateTime AtPostgresPrecision(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

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

        /// <summary>
        /// Validates the API Token for the specified user.
        /// Supports Wayfarer inbound bearer tokens stored as hashes.
        /// </summary>
        /// <param name="userId">The user ID to validate against</param>
        /// <param name="token">The API token to validate</param>
        /// <returns>True if API token is found and valid for current User</returns>
        public async Task<bool> ValidateApiTokenAsync(string userId, string token)
        {
            var context = _httpContextAccessor?.HttpContext;
            var resolver = context == null
                ? new Wayfarer.Services.IncomingApiTokenResolver(_dbContext, Wayfarer.Services.ApiWorkAdmission.TokenLookups)
                : Wayfarer.Services.IncomingApiTokenResolver.ForRequest(context, _dbContext);
            try
            {
                var user = await resolver.ResolveTokenAsync(token, context == null ? "unknown"
                    : Wayfarer.Services.RateLimitHelper.GetClientIpAddress(context));
                return user?.Id == userId;
            }
            catch (Wayfarer.Services.ApiTokenAdmissionException) { return false; }
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
                .AsNoTracking()
                .Where(t => t.UserId == userId)
                .ToListAsync();
            // Token-management presentation never receives stored third-party plaintext.
            foreach (var token in tokens) token.Token = null;
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

    /// <summary>Secret-free row identity and issuance precondition for a connection token.</summary>
    public sealed record ConnectionTokenStatus(int TokenId, DateTime IssuedAt);

    /// <summary>Minimal one-time result produced only after an immediately committed credential mutation.</summary>
    public sealed record ConnectionTokenIssue(string Token, int TokenId, DateTime IssuedAt);
}
