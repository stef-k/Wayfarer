using Wayfarer.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Verifies presentation-only itinerary ordering and label formatting.
/// </summary>
public sealed class ItineraryPresentationTests
{
    [Fact]
    public void OrderRegionsUsesDisplayOrderThenIdAndExcludesShadowFromNormalOrdinals()
    {
        var shadow = Region("00000000-0000-0000-0000-000000000010", "Unassigned Places", 0);
        var laterId = Region("00000000-0000-0000-0000-000000000003", "First by input", 5);
        var earlierId = Region("00000000-0000-0000-0000-000000000002", "2-Legitimate numeric name", 5);

        var ordered = ItineraryPresentation.OrderRegions([laterId, shadow, earlierId]);

        Assert.Equal([shadow.Id, earlierId.Id, laterId.Id], ordered.Select(region => region.Id));
        Assert.Equal(0, ItineraryPresentation.RegionOrdinal(shadow, ordered));
        Assert.Equal(1, ItineraryPresentation.RegionOrdinal(earlierId, ordered));
        Assert.Equal(2, ItineraryPresentation.RegionOrdinal(laterId, ordered));
        Assert.Equal("0-Unassigned Places", ItineraryPresentation.Label(0, shadow.Name));
        Assert.Equal("1-2-Legitimate numeric name", ItineraryPresentation.Label(1, earlierId.Name));
        Assert.Equal("2-Legitimate numeric name", earlierId.Name);
    }

    [Fact]
    public void OrderPlacesUsesExplicitNullLastDisplayOrderThenIdWhileCallersRestartOrdinalsPerRegion()
    {
        var gap = Place("00000000-0000-0000-0000-000000000023", "Galle", 20);
        var laterId = Place("00000000-0000-0000-0000-000000000022", "Colombo", 9);
        var earlierId = Place("00000000-0000-0000-0000-000000000021", "Kandy", 9);
        var laterNullId = Place("00000000-0000-0000-0000-000000000025", "Jaffna", null);
        var earlierNullId = Place("00000000-0000-0000-0000-000000000024", "Negombo", null);

        var firstRegion = ItineraryPresentation.OrderPlaces([laterNullId, gap, laterId, earlierNullId, earlierId]);
        var secondRegion = ItineraryPresentation.OrderPlaces([Place("00000000-0000-0000-0000-000000000030", "Galle", 20)]);

        Assert.Equal([earlierId.Id, laterId.Id, gap.Id, earlierNullId.Id, laterNullId.Id], firstRegion.Select(place => place.Id));
        Assert.Equal("1-Kandy", ItineraryPresentation.Label(1, firstRegion[0].Name));
        Assert.Equal("2-Colombo", ItineraryPresentation.Label(2, firstRegion[1].Name));
        Assert.Equal("1-Galle", ItineraryPresentation.Label(1, secondRegion[0].Name));
        Assert.Equal("Colombo", laterId.Name);
    }

    private static Region Region(string id, string name, int displayOrder) =>
        new() { Id = Guid.Parse(id), Name = name, DisplayOrder = displayOrder };

    private static Place Place(string id, string name, int? displayOrder) =>
        new() { Id = Guid.Parse(id), Name = name, DisplayOrder = displayOrder };
}
