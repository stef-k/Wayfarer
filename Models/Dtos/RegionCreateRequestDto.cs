using System;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to create a new Region inside a trip.
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
        /// Optional center latitude for auto-zoom. Must be provided together with <see cref="CenterLongitude"/> if present.
        /// </summary>
        public double? CenterLatitude { get; set; }

        /// <summary>
        /// Optional center longitude for auto-zoom. Must be provided together with <see cref="CenterLatitude"/> if present.
        /// </summary>
        public double? CenterLongitude { get; set; }

        /// <summary>
        /// Optional explicit display order within the trip. If omitted, server appends after existing regions (excluding the Unassigned region fixed at 0).
        /// </summary>
        public int? DisplayOrder { get; set; }
    }
}

