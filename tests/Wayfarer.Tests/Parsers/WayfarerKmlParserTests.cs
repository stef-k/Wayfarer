using System.Text;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Focused structural classification and legacy-v1 compatibility tests.</summary>
public sealed class WayfarerKmlParserTests
{
    /// <summary>Proves structurally native versionless KML selects v1 and preserves zero-waypoint custom behavior.</summary>
    [Fact]
    public void ClassifyAndParse_VersionlessNativeStructure_SelectsV1()
    {
        var tripId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var kml = $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="TripId"><value>{tripId:D}</value></Data></ExtendedData>
            <Folder><name>Segments</name><Placemark><ExtendedData>
            <Data name="SegmentId"><value>{segmentId:D}</value></Data>
            <Data name="Mode"><value>walk</value></Data><Data name="DurationMin"><value>1.5</value></Data>
            </ExtendedData><LineString><coordinates>0,0,0 1,1,0</coordinates></LineString></Placemark></Folder>
            </Document></kml>
            """;

        var parsed = Parse(kml);

        Assert.Equal(WayfarerKmlKind.NativeV1, parsed.Kind);
        var segment = Assert.Single(parsed.Document!.Segments);
        Assert.Empty(segment.WaypointPlaceIds);
        Assert.True(segment.HasCustomRoute);
        Assert.Equal(90, segment.DurationSeconds);
    }

    /// <summary>Proves explicit v1 cannot silently accept a v2-only field.</summary>
    [Fact]
    public void ClassifyAndParse_ExplicitV1WithV2Field_Rejects()
    {
        var kml = $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="WayfarerSchemaVersion"><value>1</value></Data>
            <Data name="TripId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Folder><name>Segments</name><Placemark><ExtendedData>
            <Data name="SegmentId"><value>{Guid.NewGuid():D}</value></Data>
            <Data name="HasCustomRoute"><value>true</value></Data>
            </ExtendedData><LineString><coordinates>0,0 1,1</coordinates></LineString></Placemark></Folder>
            </Document></kml>
            """;

        Assert.Throws<FormatException>(() => Parse(kml));
    }

    /// <summary>Proves malformed required identities reject instead of receiving generated replacements.</summary>
    [Fact]
    public void ClassifyAndParse_MalformedRequiredIdentity_Rejects()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="WayfarerSchemaVersion"><value>1</value></Data>
            <Data name="TripId"><value>not-a-guid</value></Data></ExtendedData>
            </Document></kml>
            """;

        Assert.Throws<FormatException>(() => Parse(kml));
    }

    /// <summary>Preserves provenance written before optional feature metadata was added to native Trip KML.</summary>
    [Fact]
    public void ClassifyAndParse_LegacyMetadataFreePlace_PreservesProvenance()
    {
        var kml = $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="TripId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Folder><name>Region</name><ExtendedData><Data name="RegionId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Placemark><name>Place</name><ExtendedData>
            <Data name="PlaceId"><value>{Guid.NewGuid():D}</value></Data>
            <Data name="AddressEnrichmentProvider"><value>geoapify</value></Data>
            <Data name="AddressEnrichmentStorageMode"><value>persistent</value></Data>
            <Data name="AddressEnrichedAt"><value>2026-08-28T12:00:00Z</value></Data>
            </ExtendedData><Point><coordinates>22.2,40.1</coordinates></Point></Placemark></Folder>
            </Document></kml>
            """;

        var place = Assert.Single(Assert.Single(Parse(kml).Document!.Regions).Places);

        Assert.Equal((null, null, "geoapify", "persistent"),
            (place.ResolvedFeatureName, place.ResolvedFeatureType,
                place.AddressEnrichmentProvider, place.AddressEnrichmentStorageMode));
    }

    /// <summary>Rejects unauthenticated Mapbox feature metadata without discarding valid Place provenance.</summary>
    [Fact]
    public void ClassifyAndParse_MapboxFeatureMetadata_ClearsFeatureFieldsAndPreservesProvenance()
    {
        var kml = $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="TripId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Folder><name>Region</name><ExtendedData><Data name="RegionId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Placemark><name>Place</name><ExtendedData>
            <Data name="PlaceId"><value>{Guid.NewGuid():D}</value></Data>
            <Data name="ResolvedFeatureName"><value>Supplied landmark</value></Data>
            <Data name="ResolvedFeatureType"><value>building</value></Data>
            <Data name="AddressEnrichmentProvider"><value>mapbox</value></Data>
            <Data name="AddressEnrichmentStorageMode"><value>permanent</value></Data>
            <Data name="AddressEnrichedAt"><value>2026-08-28T12:00:00Z</value></Data>
            </ExtendedData><Point><coordinates>22.2,40.1</coordinates></Point></Placemark></Folder>
            </Document></kml>
            """;

        var place = Assert.Single(Assert.Single(Parse(kml).Document!.Regions).Places);

        Assert.Equal((null, null, "mapbox", "permanent"),
            (place.ResolvedFeatureName, place.ResolvedFeatureType,
                place.AddressEnrichmentProvider, place.AddressEnrichmentStorageMode));
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero), place.AddressEnrichedAt);
    }

    private static (WayfarerKmlKind Kind, WayfarerKmlDocument? Document) Parse(string kml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        return WayfarerKmlParser.ClassifyAndParse(stream);
    }
}
