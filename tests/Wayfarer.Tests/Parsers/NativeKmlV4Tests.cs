using System.Text;
using System.Xml.Linq;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Personal interchange uses the typed codec and fails before generic fallback.</summary>
public sealed class NativeKmlV4Tests
{
    /// <summary>Opt-in is explicit even when empty; authored qualifiers and entry order remain literal.</summary>
    [Fact]
    public void PersonalCodecRetainsTypedValuesWhileDefaultHasNoHints()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Personal", Planning = Plan() };
        var ordinary = TripWayfarerKmlExporter.BuildKml(trip);
        Assert.DoesNotContain("Planning", ordinary);
        Assert.Contains("<value>3</value>", ordinary);
        var personal = TripWayfarerKmlExporter.BuildKml(trip, includePrivatePlanning: true);
        var parsed = Parse(personal);
        Assert.Equal(WayfarerKmlKind.NativeV4, parsed.Kind);
        Assert.True(PlanningCodec.Equal(trip.Planning, parsed.Document!.Planning));
        trip.Planning = null;
        Assert.Equal(WayfarerKmlKind.NativeV4, Parse(TripWayfarerKmlExporter.BuildKml(trip, true)).Kind);
    }

    /// <summary>V4 preserves Region covers with XML escaping; legacy/default output retains its established omission.</summary>
    [Fact]
    public void RegionCoverIsPersonalV4ContentAndOptionalForOlderFiles()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Name = "Region", CoverImageUrl = "https://example.invalid/cover?a=1&b=2" };
        trip.Regions.Add(region);
        var personal = TripWayfarerKmlExporter.BuildKml(trip, true);
        Assert.Equal(region.CoverImageUrl, Parse(personal).Document!.Regions.Single().CoverImageUrl);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var xml = XDocument.Parse(personal);
        var folder = xml.Descendants(k + "Folder").Single();
        var cover = folder.Descendants(k + "Data").Single(e => (string?)e.Attribute("name") == "CoverImageUrl");
        cover.AddAfterSelf(new XElement(cover));
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
        cover.Remove();
        folder.Descendants(k + "Data").Single(e => (string?)e.Attribute("name") == "CoverImageUrl").Remove();
        Assert.Null(Parse(xml.ToString()).Document!.Regions.Single().CoverImageUrl);
        foreach (var version in new[] { "1", "2", "3" })
        {
            var legacy = TripWayfarerKmlExporter.BuildKml(trip).Replace("<value>3</value>", $"<value>{version}</value>");
            Assert.Null(Parse(legacy).Document!.Regions.Single().CoverImageUrl);
            Assert.DoesNotContain("Planning", legacy);
        }
        foreach (var coverValue in new string?[] { null, "" })
        {
            region.CoverImageUrl = coverValue;
            Assert.Equal(coverValue, Parse(TripWayfarerKmlExporter.BuildKml(trip, true)).Document!.Regions.Single().CoverImageUrl);
        }
    }

    /// <summary>Reserved payloads cannot masquerade as legacy or third-party geography.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("5")]
    public void PlanningRequiresSupportedExplicitVersion(string? version)
    {
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(new Trip { Id = Guid.NewGuid(), Name = "Empty", Planning = Plan() }, true));
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var field = xml.Descendants(k + "Data").Single(e => (string?)e.Attribute("name") == "WayfarerSchemaVersion");
        if (version == null) field.Remove();
        else field.Element(k + "value")!.Value = version;
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
    }

    /// <summary>Unknown, duplicate, invalid and misplaced claims reject the entire document.</summary>
    [Theory]
    [InlineData("{\"format\":1,\"format\":1,\"entries\":[]}")]
    [InlineData("{\"format\":2,\"entries\":[]}")]
    [InlineData("{\"format\":1,\"entries\":[],\"original\":{}}")]
    [InlineData("null")]
    [InlineData("")]
    public void InvalidAggregateNeverBecomesOmission(string json)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(new Trip { Id = Guid.NewGuid(), Name = "Empty", Planning = Plan() }, true));
        xml.Descendants(k + "Data").Single(e => (string?)e.Attribute("name") == "Planning").Element(k + "value")!.Value = json;
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
    }

    /// <summary>Strictness applies to otherwise valid content, not just to an already-empty aggregate.</summary>
    [Theory]
    [InlineData("alias")]
    [InlineData("nested-duplicate")]
    [InlineData("field")]
    [InlineData("zone")]
    [InlineData("xml-value")]
    public void AmbiguousOrUnsupportedClaimsRejectValidSurroundingContent(string fault)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(new Trip { Id = Guid.NewGuid(), Name = "Trip", Planning = Plan() }, true));
        var data = xml.Descendants(k + "Data").Single(e => (string?)e.Attribute("name") == "Planning");
        var value = data.Element(k + "value")!;
        value.Value = fault switch
        {
            "alias" => value.Value.Replace("\"format\":1", "\"format\":1,\"Format\":1"),
            "nested-duplicate" => value.Value.Replace("\"form\":\"at\"", "\"form\":\"at\",\"form\":\"at\""),
            "field" => value.Value.Replace("\"format\":1", "\"format\":1,\"originals\":{}"),
            "zone" => value.Value.Replace("Europe/Berlin", "Unsupported/Zone"),
            _ => value.Value
        };
        if (fault == "xml-value") data.Add(new XElement(k + "value", value.Value));
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
    }

    /// <summary>Notes that contain prose are safe; XML payloads in ignored folders or namespaces are not.</summary>
    [Theory]
    [InlineData("<Planning>{}</Planning>")]
    [InlineData("<ExtendedData><Data name='Planning'><value>{}</value></Data></ExtendedData>")]
    [InlineData("<ExtendedData><Data name=' planning '><value>{}</value></Data></ExtendedData>")]
    public void MisplacedReservedDataRejectsBeforeFallback(string payload) =>
        Assert.Throws<FormatException>(() => Parse($"<kml xmlns='http://www.opengis.net/kml/2.2'><Document><Folder>{payload}</Folder></Document></kml>"));

    /// <summary>Duplicate XML coordinates are ambiguous content, even when the first geometry is usable.</summary>
    [Fact]
    public void PersonalPointCannotSilentlyChooseBetweenDuplicateCoordinateValues()
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Name = "Region" };
        region.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Place", Location = new NetTopologySuite.Geometries.Point(23, 37) { SRID = 4326 } });
        trip.Regions.Add(region);
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(trip, true));
        xml.Descendants(k + "Point").Single().Add(new XElement(k + "coordinates", "24,38,0"));
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
    }

    private static WayfarerKmlClassification Parse(string text) => WayfarerKmlParser.ClassifyAndParse(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>A Region label cannot admit misplaced Segment metadata or hide malformed Region identity.</summary>
    [Theory]
    [InlineData("segment-in-region")]
    [InlineData("invalid-region-id")]
    [InlineData("duplicate-region-id")]
    public void RegionNamedSegmentsCannotMasqueradeAsTransportContainer(string fault)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Trip" };
        trip.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = trip.Id, Name = "Segments", Planning = Plan() });
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(trip, true));
        var folder = xml.Descendants(k + "Folder").Single();
        var data = folder.Element(k + "ExtendedData")!;
        var identity = data.Elements(k + "Data").Single(d => (string?)d.Attribute("name") == "RegionId");
        if (fault == "invalid-region-id") identity.Element(k + "value")!.Value = "invalid";
        else if (fault == "duplicate-region-id") data.Add(new XElement(identity));
        else
        {
            var fields = new Dictionary<string, string> { ["SegmentId"] = Guid.NewGuid().ToString(), ["TripId"] = trip.Id.ToString(),
                ["Mode"] = "walk", ["TransportProfileKey"] = "", ["DurationTicks"] = "", ["DurationSource"] = "Automatic",
                ["DisplayOrder"] = "0", ["HasCustomRoute"] = "false", ["WaypointPlaceIds"] = "", ["WaypointRouteVertexIndices"] = "" };
            folder.Add(new XElement(k + "Placemark", new XElement(k + "ExtendedData", fields.Select(f =>
                new XElement(k + "Data", new XAttribute("name", f.Key), new XElement(k + "value", f.Value))))));
        }
        Assert.Throws<FormatException>(() => Parse(xml.ToString()));
    }

    /// <summary>Includes a repeated date and whitespace-bearing label without inferred meaning or zones.</summary>
    internal static PlanningAggregate Plan() => new(1, [
        new(Guid.NewGuid(), new("at", new("month", "2027-03")), "  Spring <&>  ", Kind: "window"),
        new(Guid.NewGuid(), new("period", End: new("known", new("date", "2027-03-15")))),
        new(Guid.NewGuid(), new("at", new("dateTime", "2027-10-31T02:30", "Europe/Berlin", 3600)), AllocationMinutes: 0),
        new(Guid.NewGuid(), new("flexible")),
        new(Guid.NewGuid(), new("at", new("month", "2027-03")), "  Return visit  ", AllocationMinutes: 90),
        new(Guid.NewGuid(), new("period", Start: new("known", new("dateTime", "2027-03-15T10:15", "UTC")), End: new("flexible"))),
        new(Guid.NewGuid(), new("unknown"))]);
}
