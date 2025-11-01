using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request for group viewport location query.
/// </summary>
public class GroupLocationsQueryRequest
{
    /// <summary>
    /// Western-most longitude of the viewport filter.
    /// </summary>
    public double MinLng { get; set; }

    /// <summary>
    /// Southern-most latitude of the viewport filter.
    /// </summary>
    public double MinLat { get; set; }

    /// <summary>
    /// Eastern-most longitude of the viewport filter.
    /// </summary>
    public double MaxLng { get; set; }

    /// <summary>
    /// Northern-most latitude of the viewport filter.
    /// </summary>
    public double MaxLat { get; set; }

    /// <summary>
    /// Client zoom level hint (used for bucketing/sampling decisions).
    /// </summary>
    public double ZoomLevel { get; set; }

    /// <summary>
    /// Optional explicit list of user Ids to include; defaults to allowed members.
    /// </summary>
    public List<string>? UserIds { get; set; }
    // Optional chronological filters

    /// <summary>
    /// Optional date scope indicator (day, month, year).
    /// </summary>
    public string? DateType { get; set; } // "day" | "month" | "year"

    /// <summary>
    /// Year component when filtering by date.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Month component when filtering by date.
    /// </summary>
    public int? Month { get; set; }

    /// <summary>
    /// Day component when filtering by date.
    /// </summary>
    public int? Day { get; set; }

    /// <summary>
    /// Optional page size requested by the caller. Defaults to configuration when not provided.
    /// </summary>
    public int? PageSize { get; set; }

    /// <summary>
    /// Continuation token supplied by the caller to advance pagination.
    /// </summary>
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Response payload for group location queries including pagination metadata.
/// </summary>
public class GroupLocationsQueryResponse
{
    /// <summary>
    /// Total number of items matching the query across all pages.
    /// </summary>
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    /// <summary>
    /// Number of locations included in the current page.
    /// </summary>
    [JsonPropertyName("returnedItems")]
    public int ReturnedItems { get; init; }

    /// <summary>
    /// Effective page size used when shaping the response.
    /// </summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    /// <summary>
    /// Indicates whether additional pages are available.
    /// </summary>
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    /// <summary>
    /// Continuation token to request the next page; null when no further data exists.
    /// </summary>
    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    /// <summary>
    /// True when the server truncated results (either within the page or because more pages remain).
    /// </summary>
    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; init; }

    /// <summary>
    /// Collection of location DTOs for the current page.
    /// </summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<PublicLocationDto> Results { get; init; }
}
