using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request for group viewport location query.
/// </summary>
public class GroupLocationsQueryRequest
{
    public double MinLng { get; set; }
    public double MinLat { get; set; }
    public double MaxLng { get; set; }
    public double MaxLat { get; set; }
    public double ZoomLevel { get; set; }
    public List<string>? UserIds { get; set; }
    // Optional chronological filters
    public string? DateType { get; set; } // "day" | "month" | "year"
    public int? Year { get; set; }
    public int? Month { get; set; }
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
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    [JsonPropertyName("returnedItems")]
    public int ReturnedItems { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<PublicLocationDto> Results { get; init; }
}
