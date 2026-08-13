namespace Wayfarer.Models.Dtos;

/// <summary>Identifies one ordered saved-Place waypoint without duplicating private Place data.</summary>
public sealed class ApiTripSegmentWaypointDto
{
    /// <summary>Gets or initializes the canonical saved Place identity.</summary>
    public Guid PlaceId { get; init; }

    /// <summary>Gets or initializes the zero-based waypoint position.</summary>
    public int Position { get; init; }

    /// <summary>Gets or initializes the zero-based custom-route vertex index, or null for fallback geometry.</summary>
    public int? RouteVertexIndex { get; init; }
}
