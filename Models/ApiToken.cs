namespace Wayfarer.Models
{
    public class ApiToken
    {
        public int Id { get; set; }

        public required string Name { get; set; } // Name of the service/purpose the token will be used for
        public required string Token { get; set; }
        public DateTime CreatedAt { get; set; }

        // Foreign key to User - every token must belong to a user
        public required string UserId { get; set; }

        // Navigation property to User
        public required ApplicationUser User { get; set; }
    }
}
