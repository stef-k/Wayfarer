using System;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Region. Trip association is immutable.
    /// Null or omitted fields preserve stored values. Reserved name/order values can only be resubmitted unchanged.
    /// A null or omitted coordinate pair preserves the stored center.
    /// </summary>
    public class RegionUpdateRequestDto
    {
        /// <summary>
        /// Optional trimmed name. Ordinary Regions cannot adopt "Unassigned Places" (case-insensitive).
        /// A reserved Region accepts only its exact stored name after trimming, as a no-op.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional notes, including for reserved Regions. Non-null input is normalized; an empty string clears notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional cover image URL, including for reserved Regions. An empty string clears the stored URL.
        /// </summary>
        public string? CoverImageUrl { get; set; }

        /// <summary>
        /// Optional finite WGS84 center latitude in the inclusive range [-90, 90], including for reserved Regions.
        /// Must be provided together with <see cref="CenterLongitude"/> when non-null.
        /// </summary>
        public double? CenterLatitude { get; set; }

        /// <summary>
        /// Optional finite WGS84 center longitude in the inclusive range [-180, 180], including for reserved Regions.
        /// Must be provided together with <see cref="CenterLatitude"/> when non-null.
        /// </summary>
        public double? CenterLongitude { get; set; }

        /// <summary>
        /// Optional explicit display order. A reserved Region accepts only its stored order, as a no-op.
        /// </summary>
        public int? DisplayOrder { get; set; }
    }
}

