using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NetTopologySuite.Geometries;

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