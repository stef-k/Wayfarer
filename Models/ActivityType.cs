namespace Wayfarer.Models
{
    public class ActivityType
    {
        public int Id { get; set; }

        // The name of the activity (e.g., "Running", "Cycling")
        public required string Name { get; set; }

        // Optional field for more details about the activity type
        public string? Description { get; set; }
    }

}
