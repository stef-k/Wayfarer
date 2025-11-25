using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models;

public class Place
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    [BindNever, ValidateNever] 
    public string UserId { get; set; } = string.Empty;

    /// <summary>Foreign key to the parent region.</summary>
    public Guid RegionId { get; set; }

    /// <summary>Navigation property to the parent region.</summary>
    [ValidateNever] 
    [JsonIgnore] 
    public Region Region { get; set; } = null!;

    /// <summary>Place name or title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Geographic coordinate of this place.</summary>
    public Point? Location { get; set; }

    /// <summary>Rich-text HTML description, may include images.</summary>
    [ValidateNever]
    public string? Notes { get; set; }

    /// <summary>Order for displaying places in the UI.</summary>
    [ValidateNever]
    public int? DisplayOrder { get; set; }

    /// <summary>Name of an icon to represent this place (e.g., "museum").</summary>
    [ValidateNever]
    public string? IconName { get; set; }

    /// <summary>Hex or named color for the map marker.</summary>
    public string? MarkerColor { get; set; }

    /// <summary>Optional street address of the place.</summary>
    public string? Address { get; set; }
}
