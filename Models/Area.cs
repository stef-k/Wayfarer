using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models
{
    /// <summary>
    /// Represents a user-drawn polygonal area within a Region.
    /// </summary>
    public class Area
    {
        /// <summary>Primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>FK to parent Region.</summary>
        public Guid RegionId { get; set; }

        /// <summary>Navigation to parent Region.</summary>
        public Region Region { get; set; } = default!;

        /// <summary>Display name of this area.</summary>
        [Required]
        [StringLength(200)]
        public string Name { get; set; } = "Area";

        /// <summary>Rich-text notes (HTML) about the area.</summary>
        [ValidateNever]
        public string? Notes { get; set; }
        
        [ValidateNever]
        public int? DisplayOrder { get; set; }

        /// <summary>Hex fill colour, e.g. "#ff6600".</summary>
        [RegularExpression("^#([0-9A-Fa-f]{6})$")]
        public string? FillHex { get; set; }

        /// <summary>Polygon geometry of this area.</summary>
        [Required(ErrorMessage="You must draw an area before saving.")]
        public Polygon Geometry { get; set; } = default!;
    }
}