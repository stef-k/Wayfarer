using System;

namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// Request body to update an existing Place. All fields are optional; only provided values are applied.
    /// </summary>
    public class PlaceUpdateRequestDto
    {
        /// <summary>
        /// Optional new region ID to move the place. Must belong to a trip owned by the same user.
        /// </summary>
        public Guid? RegionId { get; set; }

        /// <summary>
        /// Optional new name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional latitude in degrees (WGS84). Must be provided together with <see cref="Longitude"/> if present.
        /// </summary>
        public double? Latitude { get; set; }

        /// <summary>
        /// Optional longitude in degrees (WGS84). Must be provided together with <see cref="Latitude"/> if present.
        /// </summary>
        public double? Longitude { get; set; }

        /// <summary>
        /// Optional notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Optional explicit display order within the region. If omitted, order is unchanged.
        /// </summary>
        public int? DisplayOrder { get; set; }

        /// <summary>
        /// Optional new icon name. If empty/whitespace or <see cref="ClearIcon"/> is true, defaults to "marker".
        /// </summary>
        public string? IconName { get; set; }

        /// <summary>
        /// Optional new marker color. If empty/whitespace or <see cref="ClearMarkerColor"/> is true, defaults to "bg-blue".
        /// </summary>
        public string? MarkerColor { get; set; }

        /// <summary>
        /// When true, resets icon to default ("marker").
        /// </summary>
        public bool? ClearIcon { get; set; }

        /// <summary>
        /// When true, resets marker color to default ("bg-blue").
        /// </summary>
        public bool? ClearMarkerColor { get; set; }
    }
}

