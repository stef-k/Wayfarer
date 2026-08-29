namespace Wayfarer.Models.Dtos.Editor;

/// <summary>Bounded same-origin request for an explicit Trip Editor map search.</summary>
public sealed record EditorGeocodeSearchRequestDto(string? Query, int? Limit);

/// <summary>
/// Search response returned by the Trip Editor geocode proxy.
/// </summary>
public sealed record EditorGeocodeSearchResponseDto(
    string Query,
    string Attribution,
    IReadOnlyList<EditorGeocodeSearchResultDto> Results);

/// <summary>
/// Single geocode result normalized for Trip Editor search-add.
/// </summary>
public sealed record EditorGeocodeSearchResultDto(
    string Id,
    string Provider,
    string Name,
    string DisplayName,
    string Address,
    string? Category,
    string? Type,
    double Latitude,
    double Longitude);
