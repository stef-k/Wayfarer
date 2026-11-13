using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Suggestions cannot establish identity; retained format-1 facts remain authoritative.</summary>
public sealed class TripCompositionSelectionTests
{
    [Fact]
    public void TaipeiNearbySameNameIsOnlySuggestionAndPlanningDoesNotAffectIt()
    {
        var source = new Trip(); var current = new Trip();
        var a = new Region(); var b = new Region(); source.Regions.Add(a); current.Regions.Add(b);
        var p = new Place { Id = Guid.NewGuid(), Name = "Taipei 101", Location = new Point(121.5645, 25.034) };
        a.Places.Add(p);
        b.Places.Add(new Place { Id = Guid.NewGuid(), Name = p.Name, Location = new Point(121.5645, 25.04254) });
        var before = JsonSerializer.Serialize(TripCompositionMatching.Candidates(source, current, "b", "place", p.Id, [], null));
        Assert.Contains("possibleMatch", before); Assert.DoesNotContain("included", before);
        p.Planning = new(1, [new(Guid.NewGuid(), new("unknown"), "secret")]);
        Assert.Equal(before, JsonSerializer.Serialize(TripCompositionMatching.Candidates(source, current, "b", "place", p.Id, [], null)));
    }

    [Fact]
    public void LegacyInitialMappingDefaultsToAIncluded()
    {
        var mapping = JsonSerializer.Deserialize<TripCompositionMapping>("{\"kind\":\"place\",\"sourceId\":\"" + Guid.NewGuid() + "\",\"destinationId\":\"" + Guid.NewGuid() + "\"}", TripCompositionSnapshot.Json)!;
        Assert.Equal("a", mapping.Source); Assert.Equal("included", mapping.Decision);
    }
    [Fact]
    public void AreaHoleDifferenceAndPartialOverlapRemainReviewSuggestions()
    {
        var source = new Trip(); var current = new Trip(); var a = new Region(); var b = new Region();
        source.Regions.Add(a); current.Regions.Add(b);
        var shell = new LinearRing([new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)]);
        var hole = new LinearRing([new(1, 1), new(1, 2), new(2, 2), new(2, 1), new(1, 1)]);
        var area = new Area { Id = Guid.NewGuid(), Name = "Park", Geometry = new Polygon(shell, [hole]) };
        a.Areas.Add(area); b.Areas.Add(new Area { Id = Guid.NewGuid(), Name = "Park", Geometry = new Polygon(shell) });
        var result = JsonSerializer.Serialize(TripCompositionMatching.Candidates(source, current, "b", "area", area.Id, [], null));
        Assert.Contains("possibleMatch", result); Assert.Contains("contained", result); Assert.DoesNotContain("\"Geometry\":\"equal\"", result);
    }

    [Theory]
    [InlineData("append", 3)]
    [InlineData("start", 0)]
    [InlineData("after", 2)]
    public void OrderedBlocksPreserveSiblingsAndDoNotChangeCoordinates(string position, int index)
    {
        var trip = new Trip(); var region = new Region { Id = Guid.NewGuid() }; trip.Regions.Add(region);
        var old = new[] { "Museum", "Café", "Park" }.Select((name, i) => new Place { Id = Guid.NewGuid(), Name = name,
            RegionId = region.Id, DisplayOrder = i == 2 ? null : 7, Location = new Point(i, i) }).ToArray();
        // Tied/null orders use the existing stable identity comparator; expected is the actual visible sequence.
        foreach (var place in old) region.Places.Add(place);
        var ordered = old.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToArray();
        var added = new[] { "Beach", "Castle" }.Select(n => new Place { Id = Guid.NewGuid(), RegionId = region.Id, Name = n }).ToArray();
        foreach (var place in added) region.Places.Add(place);
        TripCompositionOrdering.Insert(trip, "place", region.Id, added.Select(p => p.Id).ToArray(), new(
            ordered.Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray(), position, position == "after" ? ordered[1].Id : null));
        var expected = ordered.Select(p => p.Name).ToList(); expected.InsertRange(index, ["Beach", "Castle"]);
        Assert.Equal(expected, region.Places.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        Assert.All(old, p => Assert.Equal(Array.IndexOf(old, p), p.Location!.X));
    }

    [Fact]
    public void CandidateOutputIsBoundedAndStableAcrossEnumerationOrder()
    {
        var source = new Trip(); var current = new Trip(); var a = new Region(); var b = new Region();
        source.Regions.Add(a); current.Regions.Add(b); var p = new Place { Id = Guid.NewGuid(), Name = "Museum" }; a.Places.Add(p);
        foreach (var i in Enumerable.Range(0, 30)) b.Places.Add(new Place { Id = Guid.NewGuid(), Name = "Museum" });
        var first = TripCompositionMatching.Candidates(source, current, "b", "place", p.Id, [], null);
        Assert.Equal(20, first.Count); var reverse = b.Places.Reverse().ToArray(); b.Places.Clear(); foreach (var place in reverse) b.Places.Add(place);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(TripCompositionMatching.Candidates(source, current, "b", "place", p.Id, [], null)));
    }

}
