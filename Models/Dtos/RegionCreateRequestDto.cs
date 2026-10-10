using System;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to create a new Region inside a trip.
    /// A null or omitted coordinate pair creates a Region without a center.
    /// </summary>
    public class RegionCreateRequestDto
    {
        /// <summary>
        /// Region name. Cannot be "Unassigned Places".
        /// </summary>
        [Required]
        [StringLength(200, MinimumLength = 1)]
        public string Name { get; set; } = default!;

        /// <summary>
        /// Optional notes (HTML allowed).
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional cover image URL.
        /// </summary>
        public string? CoverImageUrl { get; set; }

        /// <summary>
        /// Optional finite WGS84 center latitude in the inclusive range [-90, 90] for auto-zoom.
        /// Must be provided together with <see cref="CenterLongitude"/> when non-null.
        /// </summary>
        public double? CenterLatitude { get; set; }

        /// <summary>
        /// Optional finite WGS84 center longitude in the inclusive range [-180, 180] for auto-zoom.
        /// Must be provided together with <see cref="CenterLatitude"/> when non-null.
        /// </summary>
        public double? CenterLongitude { get; set; }

        /// <summary>
        /// Optional explicit display order within the trip. If omitted, server appends after existing regions (excluding the Unassigned region fixed at 0).
        /// </summary>
        public int? DisplayOrder { get; set; }
    }
}

