using System.Text;
using System.Xml.Linq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Portable fields cannot disappear through default output, old envelopes or generic fallback.</summary>
public sealed class NativeKmlV5Tests
{
    private static readonly XNamespace K = "http://www.opengis.net/kml/2.2";

    /// <summary>Even empty unlicensed default output requires v5; explicit fields round trip independently.</summary>
    [Fact]
    public void DefaultAndPersonalCarryV5FieldsWithoutPlanningHints()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Empty", ContributionPolicy = "CC0-1.0",
            Credits = [new("Bob", "Ideas", "Unfamiliar readable terms", Notices: "Keep this notice")] };
        foreach (var personal in new[] { false, true })
        {
            var xml = TripWayfarerKmlExporter.BuildKml(trip, personal);
            var parsed = Parse(xml).Document!;
            Assert.Equal(5, parsed.Version);
            Assert.Equal(trip.ContributionPolicy, parsed.ContributionPolicy);
            Assert.Equal(trip.Credits, parsed.Credits);
            Assert.DoesNotContain("Planning", xml);
        }
        trip.ContributionPolicy = null; trip.Credits = [];
        Assert.Equal(5, Parse(TripWayfarerKmlExporter.BuildKml(trip)).Document!.Version);
    }

    /// <summary>Downgraded, aliased, duplicated and ignored reserved claims must never become a successful generic import.</summary>
    [Theory]
    [InlineData("Credits")]
    [InlineData("ContentReuse")]
    [InlineData("Title")]
    [InlineData("credits")]
    [InlineData("WayfarerSchemaVersion")]
    public void ReservedClaimsInIgnoredXmlRefuse(string field) => Assert.Throws<FormatException>(() => Parse(
        $"<kml xmlns='{K}'><Document><Folder><ExtendedData><Data name='{field}'><value>[]</value></Data></ExtendedData></Folder></Document></kml>"));

    /// <summary>One shared codec preserves unfamiliar inherited terms and rejects unsafe or ambiguous links/data.</summary>
    [Fact]
    public void CreditsRemainReadableAndStrict()
    {
        var value = new TripCredit("Alice <script>", "Offline source", "Custom supplied text", Notices: "Notice", Modifications: "Adapted");
        Assert.Equal(value, TripCreditCodec.Read(TripCreditCodec.Write([value])).Single());
        Assert.Throws<FormatException>(() => TripCreditCodec.Read("[{\"contributor\":\"a\",\"Contributor\":\"b\",\"sourceTitle\":\"s\",\"licence\":\"l\"}]"));
        Assert.Throws<FormatException>(() => TripCreditCodec.Write([value with { SourceUrl = "javascript:alert(1)" }]));
        Assert.Throws<FormatException>(() => TripCreditCodec.Write([value with { SourceUrl = "https://user:pass@example.org" }]));
    }

    /// <summary>Published default/personal fixtures agree with complete backend fields, exact values and absence semantics.</summary>
    [Fact]
    public void SharedCrossClientFixturesRetainCompleteNativeRepresentations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Trips");
        var captured = TripCompositionSource.Read(File.ReadAllText(Path.Combine(root, "cross-client-owner.json")));
        var personal = Parse(File.ReadAllText(Path.Combine(root, "cross-client-v5-personal.kml"))).Document!;
        var ordinaryText = File.ReadAllText(Path.Combine(root, "cross-client-v5.kml"));
        var ordinary = Parse(ordinaryText).Document!;
        Assert.Equal(5, personal.Version); Assert.Equal(5, ordinary.Version);
        Assert.DoesNotContain("Planning", ordinaryText);
        Assert.Equal(captured.Trip.Credits, personal.Credits); Assert.Equal(captured.Trip.ContributionPolicy, personal.ContributionPolicy);
        Assert.True(Wayfarer.Models.Planning.PlanningCodec.Equal(captured.Trip.Planning, personal.Planning));
        var region = captured.Trip.Regions.Single(); var importedRegion = personal.Regions.Single();
        Assert.Equal(region.CoverImageUrl, importedRegion.CoverImageUrl);
        Assert.Equal(1, Assert.IsType<NetTopologySuite.Geometries.Polygon>(importedRegion.Areas.Single().Geometry).NumInteriorRings);
        Assert.Equal(4, importedRegion.Places.Count);
        Assert.Contains(importedRegion.Places, p => p.Location == null && p.Notes == null && p.Address == null);
        var sourceRoute = captured.Trip.Segments.Single(s => s.Title != null);
        var route = personal.Segments.Single(s => s.Title != null);
        Assert.Equal(sourceRoute.Title, route.Title); Assert.Equal(sourceRoute.Notes, route.Notes);
        Assert.Equal(sourceRoute.EstimatedDuration!.Value.Ticks, route.Duration!.Value.Ticks);
        Assert.Equal(sourceRoute.EstimatedDistanceKm, route.DistanceKm);
        Assert.Equal("walk", sourceRoute.RouteMappingMode);
        Assert.Contains(captured.Trip.Segments, s => s.Title == null && s.FromPlaceId == null && s.ToPlaceId == null && s.RouteGeometry == null);
        Assert.Contains(region.Places, p => p.Location == null && p.DisplayOrder == null && p.Notes == null);
        Assert.Equal("persistent", region.Places.Single(p => p.Address != null).AddressEnrichmentStorageMode);
    }

    /// <summary>V5 optional Notes preserve null versus present empty text on every target through writing and parsing.</summary>
    [Fact]
    public void NativeV5DistinguishesNullAndEmptyOptionalNotes()
    {
        var trip = TripCompositionSource.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Trips", "cross-client-owner.json"))).Trip;
        var region = trip.Regions.Single(); var place = region.Places.First(); var area = region.Areas.Single(); var segment = trip.Segments.First();
        foreach (var notes in new string?[] { null, "" })
        {
            trip.Notes = notes; region.Notes = notes; place.Notes = notes; area.Notes = notes; segment.Notes = notes;
            var parsed = Parse(TripWayfarerKmlExporter.BuildKml(trip)).Document!;
            Assert.Equal(notes, parsed.Notes); Assert.Equal(notes, parsed.Regions.Single().Notes);
            Assert.Equal(notes, parsed.Regions.Single().Places.Single(p => p.Id == place.Id).Notes);
            Assert.Equal(notes, parsed.Regions.Single().Areas.Single().Notes);
            Assert.Equal(notes, parsed.Segments.Single(s => s.Id == segment.Id).Notes);
        }
    }

    private static WayfarerKmlClassification Parse(string xml) =>
        WayfarerKmlParser.ClassifyAndParse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
}
