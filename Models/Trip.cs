using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wayfarer.Models
{
    /// <summary>
    /// Represents a trip composed of regions, places, and segments.
    /// </summary>
    public class Trip : Wayfarer.Models.Planning.IPlanningTarget
    {
        /// <summary>Owner-private typed intentions; public/raw serialization and ordinary forms never include them.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
        public Wayfarer.Models.Planning.PlanningAggregate? Planning { get; set; }

        /// <summary>Primary key.</summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>User identifier from ASP.NET Identity.</summary>
        [ValidateNever]      // Don’t try to validate or bind this from the form
        [BindNever]         // Don’t even attempt to bind it in model binding
        public string UserId { get; set; } = default!;

        /// <summary>User who owns this trip.</summary>
        [ValidateNever]      // Don’t try to validate or bind this from the form
        [BindNever]         // Don’t even attempt to bind it in model binding
        public ApplicationUser User { get; set; } = default!;

        /// <summary>Trip name or title.</summary>
        [Required]
        [StringLength(200, MinimumLength = 1)]
        public string Name { get; set; } = default!;

        /// <summary>Rich-text HTML notes, may include embedded images via &lt;img&gt; tags.</summary>
        public string? Notes { get; set; }

        /// <summary>Whether this trip is publicly shareable.</summary>
        public bool IsPublic { get; set; } = false;

        /// <summary>
        /// Whether to show visit progress in public view.
        /// Only effective when IsPublic is true.
        /// </summary>
        public bool ShareProgressEnabled { get; set; } = false;

        // Lat, Lon and Zoom for Trip set views and permalinks
        // Trip URL latitude
        public double? CenterLat { get; set; }
        // Trip URL Longitude
        public double? CenterLon { get; set; }
        // Trip URL Zoom
        public int? Zoom { get; set; }


        /// <summary>Optional cover image URL for the trip.</summary>
        [Url]
        public string? CoverImageUrl { get; set; }

        /// <summary>Timestamp of last update (including child changes).</summary>
        public DateTime UpdatedAt { get; set; }
        
        /// <summary>Collection of regions (areas) included in this trip.</summary>
        public ICollection<Region> Regions { get; set; } = new List<Region>();

        /// <summary>Collection of segments (travel legs) in this trip.</summary>
        public ICollection<Segment> Segments { get; set; } = new List<Segment>();

        /// <summary>Collection of tags applied to this trip.</summary>
        public ICollection<Tag> Tags { get; set; } = new List<Tag>();
    }
}
