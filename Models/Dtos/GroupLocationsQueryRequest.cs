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
    /// Optional bearer adapter page size, defaulted from configuration and capped by its maximum.
    /// The Web adapter ignores this input and returns its unpaged selection without pagination flags.
    /// </summary>
    public int? PageSize { get; set; }

    /// <summary>
    /// Bearer adapter anchor in a freshly recomputed selected-marker set; the Web adapter ignores it.
    /// Malformed tokens or anchors absent from the new selection restart from the beginning.
    /// Keep the query scope consistent and defensively deduplicate as selections change.
    /// This is not a stable snapshot, exactly-once iteration or complete stored-history cursor.
    /// </summary>
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Bearer adapter response for selected Group marker pages, including pagination metadata.
/// The Web adapter returns only totalItems/results for its unpaged selection.
/// </summary>
public class GroupLocationsQueryResponse
{
    /// <summary>
    /// Bearer date-query count of bbox-filtered selected markers before paging.
    /// The viewport fallback retains its underlying bbox count. This does not guarantee full stored-history completeness.
    /// </summary>
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    /// <summary>
    /// Number of selected markers included in the current bearer page.
    /// </summary>
    [JsonPropertyName("returnedItems")]
    public int ReturnedItems { get; init; }

    /// <summary>
    /// Effective bearer page size after applying the configured default and maximum.
    /// </summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    /// <summary>
    /// Bearer flag indicating remaining markers in the current selection, not remaining stored history.
    /// </summary>
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    /// <summary>
    /// Bearer anchor for the next request's recomputed selection; null when this selection is exhausted.
    /// Malformed or vanished anchors restart from the beginning; snapshot stability is not promised.
    /// </summary>
    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    /// <summary>
    /// Bearer flag: true when more selected markers remain or the whole selection exceeds this page.
    /// Can remain true on the final page; false does not establish full stored-history completeness.
    /// </summary>
    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; init; }

    /// <summary>
    /// Collection of selected location DTOs for the current bearer page.
    /// </summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<PublicLocationDto> Results { get; init; }
}
