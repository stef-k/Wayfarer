using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models;

/// <summary>
/// Represents a travel segment or leg between two places in a trip.
/// </summary>
public class Segment
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    public string UserId { get; set; }

    /// <summary>Foreign key to the parent trip.</summary>
    public Guid TripId { get; set; }

    /// <summary>Navigation property to the parent trip.</summary>
    [JsonIgnore] 
    public Trip Trip { get; set; }

    /// <summary>Optional foreign key to the starting place.</summary>
    public Guid? FromPlaceId { get; set; }

    /// <summary>Navigation property to the starting place.</summary>
    [JsonIgnore]
    public Place? FromPlace { get; set; }

    /// <summary>Optional foreign key to the destination place.</summary>
    public Guid? ToPlaceId { get; set; }

    /// <summary>Navigation property to the destination place.</summary>
    [JsonIgnore]
    public Place? ToPlace { get; set; }

    /// <summary>Mode of transport (e.g., "walk", "bike").</summary>
    public string Mode { get; set; }

    /// <summary>Geometry of the route as a LineString.</summary>
    public LineString? RouteGeometry { get; set; }

    /// <summary>Estimated duration of this segment.</summary>
    public TimeSpan? EstimatedDuration { get; set; }

    /// <summary>Estimated distance in kilometers.</summary>
    public double? EstimatedDistanceKm { get; set; }

    /// <summary>Order for displaying segments in the UI.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Rich-text HTML notes about this travel leg.</summary>
    public string? Notes { get; set; }
}