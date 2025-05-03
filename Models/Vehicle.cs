namespace Wayfarer.Models
{
    public class Vehicle
    {
        public int Id { get; set; }
        public required string PlateNumber { get; set; }  // Vehicle plate number
        public string? Type { get; set; }          // Vehicle type (car, truck, etc.)
        public string? Model { get; set; }         // Vehicle model
        public string? Status { get; set; }        // Vehicle status (e.g., "Idle", "On Task")
        public string? DriverName { get; set; }   // Optional: Name of the driver

        public string? Details { get; set; }       // Additional details about the vehicle

        public string? CoDriverName { get; set; }   // Optional: Name of the co-driver

        // JSONB field to store passengers as an array of objects
        public string? Passengers { get; set; } // JSONB array: ["Passenger1", "Passenger2", ...]

        // JSONB field to store cargo as an array of objects
        public string? Cargo { get; set; } // JSONB array: ["Object 1 other details", "Cargo object 2, with more details", ...]

        // Navigation property: A vehicle can have multiple locations

        // Navigation property for the one-to-many relationship with Location
        public ICollection<Location> Locations { get; set; } = new List<Location>();
    }
}
