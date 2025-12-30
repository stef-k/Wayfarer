namespace Wayfarer.Models
{
    /// <summary>
    /// Represents an API token for authenticating API requests.
    /// Wayfarer-generated tokens are stored as hashes for security.
    /// Third-party tokens (user-provided) are stored in plain text for usability.
    /// </summary>
    public class ApiToken
    {
        public int Id { get; set; }

        /// <summary>
        /// Name of the service/purpose the token will be used for
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Plain text token value. Null for Wayfarer-generated tokens (which use TokenHash).
        /// Only populated for third-party tokens that need to be retrieved.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// SHA-256 hash of the token for secure storage and validation.
        /// Used for Wayfarer-generated tokens. Null for third-party tokens.
        /// </summary>
        public string? TokenHash { get; set; }

        public DateTime CreatedAt { get; set; }

        // Foreign key to User - every token must belong to a user
        public required string UserId { get; set; }

        // Navigation property to User
        public required ApplicationUser User { get; set; }

        /// <summary>
        /// Returns true if this is a Wayfarer-generated token (hashed).
        /// </summary>
        public bool IsHashedToken => TokenHash != null;

        /// <summary>
        /// Gets a display-safe representation of the token.
        /// Returns masked value for hashed tokens, actual value for third-party tokens.
        /// </summary>
        public string DisplayToken => IsHashedToken ? "••••••••••••••••" : (Token ?? "");
    }
}
