namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Area. Region association is immutable.
    /// </summary>
    public class AreaUpdateRequestDto
    {
        /// <summary>
        /// Optional new name for the area.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional notes (HTML content).
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional hex fill colour, e.g. "#ff6600".
        /// </summary>
        public string? FillHex { get; set; }

        /// <summary>
        /// Optional explicit display order.
        /// </summary>
        public int? DisplayOrder { get; set; }
    }
}
