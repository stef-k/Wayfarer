using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wayfarer.Models
{
    /// <summary>
    /// Represents a trip composed of regions, places, and segments.
    /// </summary>
    public class Trip
    {
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

        /// <summary>Optional trip start date (calendar-based planning).</summary>
        public DateTime? StartDate { get; set; }

        /// <summary>Optional trip end date (calendar-based planning).</summary>
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Total trip length in days (explicit override).
        /// If null, computed from dates, region totals, or segment durations.
        /// </summary>
        public int? Days { get; set; }

        /// <summary>Rich-text HTML notes, may include embedded images via &lt;img&gt; tags.</summary>
        public string? NotesHtml { get; set; }

        /// <summary>Whether this trip is publicly shareable.</summary>
        public bool IsPublic { get; set; } = false;

        /// <summary>Optional cover image URL for the trip.</summary>
        [Url]
        public string? CoverImageUrl { get; set; }

        /// <summary>Timestamp of last update (including child changes).</summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>Collection of regions (areas) included in this trip.</summary>
        public ICollection<Region>? Regions { get; set; } = new List<Region>();

        /// <summary>Collection of segments (travel legs) in this trip.</summary>
        public ICollection<Segment>? Segments { get; set; } = new List<Segment>();

        /// <summary>
        /// Computed days fallback: uses explicit Days, or date range, or sums of children.
        /// Not mapped to database.
        /// </summary>
        [NotMapped]
        public int ComputedDays
        {
            get
            {
                if (Days.HasValue)
                    return Days.Value;

                if (StartDate.HasValue && EndDate.HasValue)
                    return (EndDate.Value.Date - StartDate.Value.Date).Days + 1;

                int regionSum = 0;
                if (Regions != null)
                    regionSum = Regions.Sum(r => r.Days ?? 0);

                int segmentSum = 0;
                if (Segments != null)
                    segmentSum = (int)Segments.Sum(s => s.EstimatedDuration?.TotalDays ?? 0);

                return regionSum + segmentSum;
            }
        }
    }
}
