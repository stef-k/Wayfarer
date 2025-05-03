namespace Wayfarer.Models
{
    public class ApiToken
    {
        public int Id { get; set; }

        public required string Name { get; set; } // Name of the service/purpose the token will be used for
        public required string Token { get; set; }
        public DateTime CreatedAt { get; set; }

        // Foreign key to User (nullable, meaning it's optional)
        public string? UserId { get; set; }  // Nullable Foreign Key

        // Navigation property to User (Optional relationship)
        public ApplicationUser? User { get; set; }  // Nullable navigation property
    }
}
