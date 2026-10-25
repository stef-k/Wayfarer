namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Payload for the rejected unconditional Trip PUT; use the original-aware metadata contract.
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
