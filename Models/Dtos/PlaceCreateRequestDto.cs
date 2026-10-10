using System;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to create a new Place under a trip's region.
    /// If <see cref="RegionId"/> is not provided, the place will be created under the trip's
    /// special "Unassigned Places" region (auto-created if missing).
    /// A null or omitted coordinate pair creates a Place without a location.
    /// </summary>
    public class PlaceCreateRequestDto
    {
        /// <summary>
        /// Optional destination region ID. If null, the server uses the "Unassigned Places" region of the trip.
        /// </summary>
        public Guid? RegionId { get; set; }

        /// <summary>
        /// Place name. Required.
        /// </summary>
        [Required]
        [StringLength(200, MinimumLength = 1)]
        public string Name { get; set; } = default!;

        /// <summary>
        /// Optional finite WGS84 latitude in the inclusive range [-90, 90].
        /// Must be provided together with <see cref="Longitude"/> when non-null.
        /// </summary>
        public double? Latitude { get; set; }

        /// <summary>
        /// Optional finite WGS84 longitude in the inclusive range [-180, 180].
        /// Must be provided together with <see cref="Latitude"/> when non-null.
        /// </summary>
        public double? Longitude { get; set; }

        /// <summary>
        /// Optional rich-text notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional explicit display order within the region. If omitted, server appends at the end.
        /// </summary>
        public int? DisplayOrder { get; set; }

        /// <summary>
        /// Optional icon name. If missing/empty, defaults to <c>marker</c>.
        /// </summary>
        public string? IconName { get; set; }

        /// <summary>
        /// Optional marker color. If missing/empty, defaults to <c>bg-blue</c>.
        /// </summary>
        public string? MarkerColor { get; set; }
    }
}

