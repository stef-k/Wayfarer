using System;
using System.Collections.Generic;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Models.ViewModels;

/// <summary>
/// View model for the public trips index page.
/// Contains the list of trips and all filter/sort/pagination state.
/// </summary>
public sealed class PublicTripIndexVm
{
    /// <summary>List of public trips to display.</summary>
    public IReadOnlyList<PublicTripIndexItem> Items { get; init; } = Array.Empty<PublicTripIndexItem>();

    /// <summary>Search query text (searches Name and Notes).</summary>
    public string? Q { get; init; }

    /// <summary>View mode: "grid" (default) or "list".</summary>
    public string View { get; init; } = "grid";

    /// <summary>Sort option: "updated_desc" (default), "name_asc", or "name_desc".</summary>
    public string Sort { get; init; } = "updated_desc";

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; init; } = 24;

    /// <summary>Total number of trips matching the current filters.</summary>
    public int Total { get; init; }

    /// <summary>Total number of pages.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)Total / PageSize) : 0;

    /// <summary>Whether there is a previous page.</summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>Whether there is a next page.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>Comma-separated list of selected tag slugs (for persistency).</summary>
    public string? TagsCsv { get; init; }

    /// <summary>Filter mode: "all" (default) or "any".</summary>
    public string TagMode { get; init; } = "all";

    /// <summary>Tags currently applied to the filter.</summary>
    public IReadOnlyList<TripTagDto> SelectedTags { get; init; } = Array.Empty<TripTagDto>();

    /// <summary>Popular tags for quick filtering.</summary>
    public IReadOnlyList<TagSuggestionDto> PopularTags { get; init; } = Array.Empty<TagSuggestionDto>();
}
