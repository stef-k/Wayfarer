using System;
using System.Collections.Generic;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Lightweight trip summary for public trips index listing.
/// Contains minimal data for browsing without full regions/segments.
/// </summary>
public class ApiPublicTripSummaryDto
{
    /// <summary>Trip identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Trip name or title.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Display name of the trip owner (for attribution).</summary>
    public string? OwnerDisplayName { get; set; }

    /// <summary>Plain text excerpt of trip notes (HTML stripped, ~140 characters).</summary>
    public string? NotesExcerpt { get; set; }

    /// <summary>HTML notes limited to 200 words for trip description.</summary>
    public string? Notes { get; set; }

    /// <summary>Optional cover image URL.</summary>
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

    /// <summary>Number of places across all regions.</summary>
    public int PlacesCount { get; set; }

    /// <summary>Number of segments in this trip.</summary>
    public int SegmentsCount { get; set; }

    /// <summary>Whether current user owns this trip.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Tags associated with this trip.</summary>
    public IReadOnlyList<TripTagDto> Tags { get; set; } = Array.Empty<TripTagDto>();
}
