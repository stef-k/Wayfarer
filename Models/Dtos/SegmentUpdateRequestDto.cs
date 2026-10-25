namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Payload for the rejected unconditional Segment Notes PUT; use the protected Notes contract.
    /// </summary>
    public class SegmentUpdateRequestDto
    {
        /// <summary>
        /// Optional notes (HTML content). Set to null to clear notes.
        /// </summary>
        public string? Notes { get; set; }
    }
}
