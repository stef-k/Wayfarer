using Wayfarer.Models;

namespace Wayfarer.Util;

/// <summary>
/// Provides deterministic, presentation-only itinerary ordering and labels for Razor views.
/// </summary>
public static class ItineraryPresentation
{
    private const string ShadowRegionName = "Unassigned Places";

    /// <summary>Orders complete region entities by persisted order and stable identifier.</summary>
    public static IReadOnlyList<Region> OrderRegions(IEnumerable<Region>? regions) =>
        (regions ?? []).OrderBy(region => region.DisplayOrder).ThenBy(region => region.Id).ToList();

    /// <summary>Orders complete sibling places by persisted order and stable identifier.</summary>
    public static IReadOnlyList<Place> OrderPlaces(IEnumerable<Place>? places) =>
        (places ?? []).OrderBy(place => place.DisplayOrder).ThenBy(place => place.Id).ToList();

    /// <summary>Resolves the displayed region ordinal without letting the shadow region consume a normal number.</summary>
    public static int RegionOrdinal(Region region, IReadOnlyList<Region> orderedRegions) =>
        IsShadowRegion(region)
            ? 0
            : orderedRegions.TakeWhile(candidate => candidate.Id != region.Id).Count(candidate => !IsShadowRegion(candidate)) + 1;

    /// <summary>Formats an ordinal and untouched entity name using the itinerary display contract.</summary>
    public static string Label(int ordinal, string name) => $"{ordinal}-{name}";

    /// <summary>Identifies the built-in shadow region using the viewer's existing name contract.</summary>
    public static bool IsShadowRegion(Region region) =>
        string.Equals(region.Name, ShadowRegionName, StringComparison.Ordinal);
}
