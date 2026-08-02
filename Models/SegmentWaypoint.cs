using System.Text.Json.Serialization;

namespace Wayfarer.Models;

/// <summary>Associates an ordered intermediate saved place with one segment route.</summary>
public sealed class SegmentWaypoint
{
    /// <summary>Foreign key to the owning segment.</summary>
    public Guid SegmentId { get; set; }

    /// <summary>Owning segment navigation.</summary>
    [JsonIgnore]
    public Segment Segment { get; set; } = null!;

    /// <summary>Foreign key to the referenced saved place.</summary>
    public Guid PlaceId { get; set; }

    /// <summary>Referenced saved place navigation.</summary>
    [JsonIgnore]
    public Place Place { get; set; } = null!;

    /// <summary>Zero-based contiguous position among the segment's intermediate places.</summary>
    public int Position { get; set; }

    /// <summary>Zero-based custom-route vertex index, or null when the segment uses fallback geometry.</summary>
    public int? RouteVertexIndex { get; set; }
}
