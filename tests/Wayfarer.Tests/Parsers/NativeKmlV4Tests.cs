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

    /// <summary>Notes that contain prose are safe; XML payloads in ignored folders or namespaces are not.</summary>
    [Theory]
    [InlineData("<Planning>{}</Planning>")]
    [InlineData("<ExtendedData><Data name='Planning'><value>{}</value></Data></ExtendedData>")]
    [InlineData("<ExtendedData><Data name=' planning '><value>{}</value></Data></ExtendedData>")]
    public void MisplacedReservedDataRejectsBeforeFallback(string payload) =>
        Assert.Throws<FormatException>(() => Parse($"<kml xmlns='http://www.opengis.net/kml/2.2'><Document><Folder>{payload}</Folder></Document></kml>"));

    private static WayfarerKmlClassification Parse(string text) => WayfarerKmlParser.ClassifyAndParse(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>Includes a repeated date and whitespace-bearing label without inferred meaning or zones.</summary>
    internal static PlanningAggregate Plan() => new(1, [
        new(Guid.NewGuid(), new("at", new("month", "2027-03")), "  Spring <&>  "),
        new(Guid.NewGuid(), new("period", End: new("known", new("date", "2027-03-15")))),
        new(Guid.NewGuid(), new("at", new("dateTime", "2027-10-31T02:30", "Europe/Berlin", 3600)), AllocationMinutes: 0),
        new(Guid.NewGuid(), new("flexible"))]);
}
