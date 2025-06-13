using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wayfarer.Models;

/// <summary>
/// Represents a geographic region or section within a trip (e.g., a city or area).
/// </summary>
public class Region
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    public string UserId { get; set; }

    /// <summary>Foreign key to the parent trip.</summary>
    public Guid TripId { get; set; }

    /// <summary>Navigation property to the parent trip.</summary>
    public Trip Trip { get; set; }

    /// <summary>Region name or title.</summary>
    public string Name { get; set; }

    /// <summary>Number of days allocated to this region.</summary>
    public int? Days { get; set; }

    /// <summary>Center point for map auto-zoom.</summary>
    [ValidateNever]
    public Point? Center { get; set; }

    /// <summary>Optional boundary polygon for the region.</summary>
    [ValidateNever]
    public Polygon? Boundary { get; set; }

    /// <summary>Rich-text HTML notes about the region.</summary>
    public string? NotesHtml { get; set; }

    /// <summary>Order for displaying regions in the UI.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Toggle visibility of this region on the map.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Optional cover image URL for the region.</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Collection of places within this region.</summary>
    public ICollection<Place>? Places { get; set; } =  new List<Place>();
}