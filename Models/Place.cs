using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wayfarer.Models;

public class Place
{
        /// <summary>Primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>User identifier from ASP.NET Identity.</summary>
        public string UserId { get; set; }

        /// <summary>Foreign key to the parent region.</summary>
        public Guid RegionId { get; set; }

        /// <summary>Navigation property to the parent region.</summary>
        public Region Region { get; set; }

        /// <summary>Place name or title.</summary>
        public string Name { get; set; }

        /// <summary>Suggested time to spend at this place (e.g., 2h, 1d).</summary>
        public TimeSpan? SuggestedDuration { get; set; }

        /// <summary>Geographic coordinate of this place.</summary>
        public Point? Location { get; set; }

        /// <summary>
        /// Optional route trace within the place (e.g., walking path).
        /// </summary>
        public LineString? RouteTrace { get; set; }

        /// <summary>Rich-text HTML description, may include images.</summary>
        [ValidateNever]
        public string? DescriptionHtml { get; set; }

        /// <summary>Order for displaying places in the UI.</summary>
        [ValidateNever]
        public int? DisplayOrder { get; set; }

        /// <summary>Toggle visibility of this place on the map.</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Name of an icon to represent this place (e.g., "museum").</summary>
        [ValidateNever]
        public string? IconName { get; set; }

        /// <summary>Hex or named color for the map marker.</summary>
        public string? MarkerColor { get; set; }

        /// <summary>Optional street address of the place.</summary>
        public string? Address { get; set; }

        /// <summary>Optional website URL.</summary>
        public string? WebsiteUrl { get; set; }

        /// <summary>Optional contact phone number.</summary>
        public string? PhoneNumber { get; set; }

        /// <summary>Optional price category (e.g., $, $$, $$$).</summary>
        public string? PriceCategory { get; set; }

        /// <summary>Optional JSON for complex opening hours.</summary>
        public string? OpeningHoursJson { get; set; }
}