namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Segment's notes.
    /// </summary>
    public class SegmentUpdateRequestDto
    {
        /// <summary>
        /// Optional notes (HTML content). Set to null to clear notes.
        /// </summary>
        public string? Notes { get; set; }
    }
}
