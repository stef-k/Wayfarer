namespace Wayfarer.Models.ViewModels;

/// <summary>
/// Represents a single public trip item in the public trips index.
/// Contains only the minimal data needed for listing and preview.
/// </summary>
public sealed class PublicTripIndexItem
{
    /// <summary>Trip identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Trip name or title.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Plain text excerpt of the trip notes (HTML stripped, ~140 characters).</summary>
    public string? NotesExcerpt { get; set; }

    /// <summary>Optional cover image URL for the trip.</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Center latitude for map positioning.</summary>
    public double? CenterLat { get; set; }

    /// <summary>Center longitude for map positioning.</summary>
    public double? CenterLon { get; set; }

    /// <summary>Zoom level for map positioning.</summary>
    public int? Zoom { get; set; }

    /// <summary>Timestamp of last update.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Number of regions in this trip.</summary>
    public int RegionsCount { get; set; }

    /// <summary>Number of places across all regions in this trip.</summary>
    public int PlacesCount { get; set; }

    /// <summary>Number of segments in this trip.</summary>
    public int SegmentsCount { get; set; }

    /// <summary>Resolved thumbnail/map image URL for display.</summary>
    public string? ThumbUrl { get; set; }
}
