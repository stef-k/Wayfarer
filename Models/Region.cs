using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models;

/// <summary>
/// Represents a geographic region or section within a trip (e.g., a city or area).
/// </summary>
public class Region : Wayfarer.Models.Planning.IPlanningTarget
{
        /// <summary>Owner-private typed intentions; public/raw serialization and ordinary forms never include them.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
        public Wayfarer.Models.Planning.PlanningAggregate? Planning { get; set; }

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Foreign key to the parent trip.</summary>
    public Guid TripId { get; set; }

    /// <summary>Navigation property to the parent trip.</summary>
    [JsonIgnore] 
    public Trip Trip { get; set; } = null!;

    /// <summary>Region name or title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Center point for map auto-zoom.</summary>
    [ValidateNever]
    public Point? Center { get; set; }

    /// <summary>Rich-text HTML notes about the region.</summary>
    public string? Notes { get; set; }

    /// <summary>Order for displaying regions in the UI.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Optional cover image URL for the region.</summary>
    public string? CoverImageUrl { get; set; }
    

    /// <summary>Collection of places within this region.</summary>
    public ICollection<Place> Places { get; set; } = new List<Place>();
    
    /// <summary>Collection of drawn areas within this region.</summary>
    public ICollection<Area> Areas { get; set; } = new List<Area>();
}
