using System;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Region. Trip association is immutable.
    /// </summary>
    public class RegionUpdateRequestDto
    {
        /// <summary>
        /// Optional new name. Cannot be set to "Unassigned Places".
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional cover image URL.
        /// </summary>
        public string? CoverImageUrl { get; set; }

        /// <summary>
        /// Optional new center latitude for auto-zoom. Must be provided together with <see cref="CenterLongitude"/> if present.
        /// </summary>
        public double? CenterLatitude { get; set; }

        /// <summary>
        /// Optional new center longitude for auto-zoom. Must be provided together with <see cref="CenterLatitude"/> if present.
        /// </summary>
        public double? CenterLongitude { get; set; }

        /// <summary>
        /// Optional explicit display order. If omitted, unchanged.
        /// </summary>
        public int? DisplayOrder { get; set; }
    }
}

