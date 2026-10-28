using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Area ring fidelity at the native and generic Trip KML production seams.</summary>
public sealed class PolygonKmlRoundTripTests : TestBase
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    /// <summary>Native export, detached parsing and aggregate mapping retain every ordered ring.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeRoundTrip_PreservesRings(int holes)
    {
        var trip = CreateTrip(holes);
        var expected = trip.Regions.Single().Areas.Single().Geometry;
        var xml = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(trip));
        AssertBoundaries(xml, expected);
        using var stream = Stream(xml.ToString());
        var parsed = WayfarerKmlParser.ClassifyAndParse(stream);
        Assert.Equal(WayfarerKmlKind.NativeV3, parsed.Kind);
        var mapped = WayfarerKmlAggregateMapper.Map(parsed.Document!, "destination", new Dictionary<string, TransportProfile>(), true);
        AssertPolygon(expected, mapped.Regions.Single().Areas.Single().Geometry);
        AssertPolygon(CreatePolygon(holes), expected);
    }

    /// <summary>The My Maps exporter and generic importer preserve the same 2D Area footprint.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GoogleRoundTrip_PreservesRings(int holes)
    {
        using var db = CreateDbContext();
        var trip = CreateTrip(holes);
        db.Trips.Add(trip);
        db.SaveChanges();
        var service = new TripExportService(db, null!, null!, null!, null!,
            NullLogger<TripExportService>.Instance, Mock.Of<IConfiguration>(), null!, Mock.Of<IImageProxyService>());
        var xml = XDocument.Parse(service.GenerateGoogleMyMapsKml(trip.Id));
        var expected = trip.Regions.Single().Areas.Single().Geometry;
        AssertBoundaries(xml, expected);
        using var stream = Stream(xml.ToString());
        var imported = GoogleMyMapsKmlParser.Parse(stream, "destination");
        AssertPolygon(expected, imported.Regions.Single().Areas.Single().Geometry);
        AssertPolygon(CreatePolygon(holes), expected);
    }

    /// <summary>Standard inner boundaries work in recognized versionless, v1, v2 and v3 native files.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeParser_AllSupportedVersions_ReadsStandardBoundaries(int version)
    {
        using var stream = Stream(Document(ValidPolygon, version));
        var source = WayfarerKmlParser.ClassifyAndParse(stream).Document!;
        var mapped = WayfarerKmlAggregateMapper.Map(source, "destination", new Dictionary<string, TransportProfile>(), true);
        AssertPolygon(CreatePolygon(2), mapped.Regions.SelectMany(region => region.Areas).Single().Geometry);
    }

    /// <summary>Handwritten generic boundaries cannot be lost even if the exporter is also wrong.</summary>
    [Fact]
    public void GoogleParser_ReadsEveryStandardBoundary()
    {
        using var stream = Stream(Document(ValidPolygon));
        var trip = GoogleMyMapsKmlParser.Parse(stream, "destination");
        AssertPolygon(CreatePolygon(2), trip.Regions.Single().Areas.Single().Geometry);
    }

    /// <summary>Neither importer may salvage valid rings from a malformed or topologically invalid Area.</summary>
    [Theory]
    [MemberData(nameof(InvalidPolygons))]
    public void Parsers_RejectCompleteInvalidPolygon(string polygon)
    {
        using var native = Stream(Document(polygon, 3));
        Assert.Throws<FormatException>(() => WayfarerKmlParser.ClassifyAndParse(native));
        using var generic = Stream(Document(polygon));
        Assert.Throws<FormatException>(() => GoogleMyMapsKmlParser.Parse(generic, "destination"));
    }

    /// <summary>Representative structural, coordinate and topology failures, including bad later holes.</summary>
    public static IEnumerable<object[]> InvalidPolygons()
    {
        var shell = Boundary("outerBoundaryIs", "0,0 10,0 10,10 0,10 0,0");
        var hole = Boundary("innerBoundaryIs", "1,1 1,3 3,3 3,1 1,1");
        yield return ["<Polygon/>"];
        yield return [$"<Polygon>{hole}</Polygon>"];
        yield return [$"<Polygon>{shell}{shell}</Polygon>"];
        yield return [$"<Polygon>{shell}<innerBoundaryIs/></Polygon>"];
        yield return [$"<Polygon>{shell}<innerBoundaryIs><LinearRing/></innerBoundaryIs></Polygon>"];
        yield return [$"<Polygon>{shell}<innerBoundaryIs><LinearRing><coordinates>1,1 2,1 2,2 1,1</coordinates><coordinates>6,6 7,6 7,7 6,6</coordinates></LinearRing></innerBoundaryIs></Polygon>"];
        foreach (var invalidRing in new[]
        {
            "1,1 2,1 2,2", // Unclosed, with too few positions.
            "1,1 2,1 2,2 1,2", // Enough positions, but unclosed.
            "1,1 1,1 1,1 1,1", // Degenerate topology.
            "NaN,1 2,1 2,2 NaN,1", "181,1 182,1 182,2 181,1",
            "1,91 2,91 2,92 1,91", "1,1,bad 2,1,0 2,2,0 1,1,0",
            "1,1,0,4 2,1 2,2 1,1", "bad,1 2,1 2,2 bad,1",
            "11,11 12,11 12,12 11,11", // Hole outside shell.
            "2,2 4,2 4,4 2,2" // Overlaps the first hole.
        })
            yield return [$"<Polygon>{shell}{hole}{Boundary("innerBoundaryIs", invalidRing)}</Polygon>"];
        yield return [$"<Polygon>{Boundary("outerBoundaryIs", "0,0 10,10 0,10 10,0 0,0")}</Polygon>"];
        yield return [$"<Polygon>{shell}</Polygon><Polygon>{shell}</Polygon>"];
    }

    /// <summary>Creates a standard boundary without changing the supplied coordinate tokens.</summary>
    private static string Boundary(string name, string coordinates) =>
        $"<{name}><LinearRing><coordinates>{coordinates}</coordinates></LinearRing></{name}>";

    private const string ValidPolygon = """
        <Polygon>
        <outerBoundaryIs><LinearRing><coordinates>0,0 10.123456789012345,0 10.123456789012345,10 0,10 0,0</coordinates></LinearRing></outerBoundaryIs>
        <innerBoundaryIs><LinearRing><coordinates>1,1 1,3 3,3 3,1 1,1</coordinates></LinearRing></innerBoundaryIs>
        <innerBoundaryIs><LinearRing><coordinates>6,6 8,6 8,8 6,8 6,6</coordinates></LinearRing></innerBoundaryIs>
        </Polygon>
        """;

    /// <summary>Builds a valid Area with deliberately different ring orientations and double precision.</summary>
    private static Polygon CreatePolygon(int holes)
    {
        var polygon = (Polygon)new WKTReader().Read("POLYGON ((0 0, 10.123456789012345 0, 10.123456789012345 10, 0 10, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1), (6 6, 8 6, 8 8, 6 8, 6 6))");
        return new Polygon((LinearRing)polygon.ExteriorRing.Copy(), Enumerable.Range(0, holes)
            .Select(index => (LinearRing)polygon.GetInteriorRingN(index).Copy()).ToArray()) { SRID = 4326 };
    }

    /// <summary>Creates only the Trip/Region/Area graph needed by the production serializers.</summary>
    private static Trip CreateTrip(int holes)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Ring fidelity", UpdatedAt = DateTime.UtcNow };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Region" };
        region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Area", Geometry = CreatePolygon(holes) });
        trip.Regions.Add(region);
        return trip;
    }

    /// <summary>Checks the wire ring structure independently of either parser.</summary>
    private static void AssertBoundaries(XDocument xml, Polygon expected)
    {
        var polygon = Assert.Single(xml.Descendants(Kml + "Polygon"));
        Assert.Single(polygon.Elements(Kml + "outerBoundaryIs"));
        Assert.Equal(expected.NumInteriorRings, polygon.Elements(Kml + "innerBoundaryIs").Count());
        var coordinateCounts = polygon.Descendants(Kml + "coordinates")
            .Select(item => item.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(new[] { expected.ExteriorRing.NumPoints }.Concat(Enumerable.Range(0, expected.NumInteriorRings)
            .Select(index => expected.GetInteriorRingN(index).NumPoints)), coordinateCounts);
    }

    /// <summary>Exact equality detects ring reordering/normalization; topology equality detects footprint changes.</summary>
    private static void AssertPolygon(Polygon expected, Polygon actual)
    {
        Assert.Equal(4326, actual.SRID);
        Assert.True(actual.IsValid);
        Assert.Equal(expected.NumInteriorRings, actual.NumInteriorRings);
        Assert.True(expected.EqualsExact(actual));
        Assert.True(expected.EqualsTopologically(actual));
    }

    /// <summary>Wraps a standard polygon with only the native identities needed by each supported version.</summary>
    private static string Document(string polygon, int? version = null) => $"""
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Trip</name>
        <ExtendedData>{(version > 0 ? $"<Data name=\"WayfarerSchemaVersion\"><value>{version}</value></Data>" : "")}
        <Data name="TripId"><value>00000000-0000-0000-0000-000000000001</value></Data></ExtendedData>
        <Folder><name>Region</name>{(version.HasValue ? "<ExtendedData><Data name=\"RegionId\"><value>00000000-0000-0000-0000-000000000002</value></Data></ExtendedData>" : "")}
        <Placemark><name>Area</name><ExtendedData><Data name="AreaId"><value>00000000-0000-0000-0000-000000000003</value></Data></ExtendedData>
        {polygon}</Placemark></Folder></Document></kml>
        """;

    private static MemoryStream Stream(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
