namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Trip's metadata.
    /// Only provided fields will be updated (partial update).
    /// </summary>
    public class TripUpdateRequestDto
    {
        /// <summary>
        /// Optional new name for the trip.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional notes (HTML content).
        /// </summary>
        public string? Notes { get; set; }
    }
}
