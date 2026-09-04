namespace Wayfarer.Models
{
    /// <summary>
    /// Represents an API token for authenticating API requests.
    /// Wayfarer-generated tokens are stored as hashes for security.
    /// Historical plaintext values remain only for the bounded Mapbox credential migration.
    /// </summary>
    public class ApiToken
    {
        public int Id { get; set; }

        /// <summary>
        /// Name of the service/purpose the token will be used for
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Historical plaintext value retained only for the bounded legacy Mapbox migration.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// SHA-256 hash of the token for secure storage and validation.
        /// Used for every current inbound Wayfarer token.
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
        /// Gets a fixed display-safe representation without redisplaying stored provider credentials.
        /// </summary>
        public string DisplayToken => "••••••••••••••••";
    }
}
