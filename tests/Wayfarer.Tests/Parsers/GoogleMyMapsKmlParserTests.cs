using System.Text;
using NetTopologySuite.Geometries;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the GoogleMyMapsKmlParser which parses Google MyMaps KML format into Trip objects.
/// </summary>
public class GoogleMyMapsKmlParserTests
{
    /// <summary>
    /// Creates a memory stream from a KML XML string for testing.
    /// </summary>
    private static MemoryStream CreateStream(string kml)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(kml));
    }

    private const string KmlHeader = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"">";
    private const string KmlFooter = @"</kml>";

    [Fact]
    public void Parse_MinimalKml_ReturnsTripWithDefaults()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Test Trip</name>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.NotNull(trip);
        Assert.Equal("Test Trip", trip.Name);
        Assert.Equal("user1", trip.UserId);
        Assert.NotEqual(Guid.Empty, trip.Id);
        Assert.Empty(trip.Regions);
        Assert.Empty(trip.Segments);
    }

    [Fact]
    public void Parse_KmlWithFolder_CreatesRegion()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip with Region</name>
    <Folder>
        <name>New York</name>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Equal("New York", region.Name);
        Assert.Equal(trip.Id, region.TripId);
        Assert.Equal("user1", region.UserId);
    }

    [Fact]
    public void Parse_FolderWithPrefixedName_StripsPrefixFromRegionName()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>01 – Manhattan</name>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        Assert.Equal("Manhattan", trip.Regions.First().Name);
    }

    [Fact]
    public void Parse_PlacemarkWithPoint_CreatesPlace()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Times Square</name>
            <description>Famous intersection</description>
            <Point>
                <coordinates>-73.9855,40.7580,0</coordinates>
            </Point>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        var place = region.Places.First();
        Assert.Equal("Times Square", place.Name);
        Assert.Equal("Famous intersection", place.Notes);
        Assert.Equal(40.7580, place.Location.Y, 4);
        Assert.Equal(-73.9855, place.Location.X, 4);
        Assert.Equal("user1", place.UserId);
    }

    [Fact]
    public void Parse_PlacemarkWithLineString_CreatesSegment()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>drive</name>
            <LineString>
                <coordinates>-74.0060,40.7128,0 -73.9855,40.7580,0</coordinates>
            </LineString>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Segments);
        var segment = trip.Segments.First();
        Assert.Equal("drive", segment.Mode);
        Assert.Equal(trip.Id, segment.TripId);
        Assert.Equal("user1", segment.UserId);
        Assert.IsType<LineString>(segment.RouteGeometry);
        Assert.Equal(2, ((LineString)segment.RouteGeometry).NumPoints);
    }

    [Fact]
    public void Parse_PlacemarkWithPolygon_CreatesArea()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Central Park</name>
            <description>Large park</description>
            <Polygon>
                <outerBoundaryIs>
                    <LinearRing>
                        <coordinates>-74.0,40.7 -73.9,40.7 -73.9,40.8 -74.0,40.8 -74.0,40.7</coordinates>
                    </LinearRing>
                </outerBoundaryIs>
            </Polygon>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Single(region.Areas);
        var area = region.Areas.First();
        Assert.Equal("Central Park", area.Name);
        Assert.Equal("Large park", area.Notes);
        Assert.IsType<Polygon>(area.Geometry);
    }

    [Fact]
    public void Parse_SegmentOutsideFolder_AddsToTripSegments()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Placemark>
        <name>route</name>
        <LineString>
            <coordinates>-74.0060,40.7128,0 -73.9855,40.7580,0</coordinates>
        </LineString>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Segments);
        Assert.Equal("route", trip.Segments.First().Mode);
    }

    [Fact]
    public void Parse_MultiplePlaces_CalculatesCenterCoordinates()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Place 1</name>
            <Point><coordinates>-74.0,40.0,0</coordinates></Point>
        </Placemark>
        <Placemark>
            <name>Place 2</name>
            <Point><coordinates>-72.0,42.0,0</coordinates></Point>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Equal(41.0, trip.CenterLat!.Value, 1); // Average of 40 and 42
        Assert.Equal(-73.0, trip.CenterLon!.Value, 1); // Average of -74 and -72
        Assert.Equal(5, trip.Zoom);
    }

    [Fact]
    public void Parse_WithExtendedDataTags_ParsesTagsCorrectly()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document xmlns:wf=""https://wayfarer.stefk.me/kml"">
    <name>Tagged Trip</name>
    <ExtendedData>
        <wf:Tags>adventure,beach,road-trip</wf:Tags>
    </ExtendedData>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.NotNull(trip.Tags);
        Assert.Equal(3, trip.Tags.Count);
        Assert.Contains(trip.Tags, t => t.Slug == "adventure");
        Assert.Contains(trip.Tags, t => t.Slug == "beach");
        Assert.Contains(trip.Tags, t => t.Slug == "road-trip");
    }

    [Fact]
    public void Parse_MultipleRegions_CreatesAllRegions()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Multi-region Trip</name>
    <Folder><name>Region 1</name></Folder>
    <Folder><name>Region 2</name></Folder>
    <Folder><name>Region 3</name></Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Equal(3, trip.Regions.Count);
        var regionNames = trip.Regions.Select(r => r.Name).ToList();
        Assert.Contains("Region 1", regionNames);
        Assert.Contains("Region 2", regionNames);
        Assert.Contains("Region 3", regionNames);
    }

    [Fact]
    public void Parse_MixedContent_ParsesAllElements()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Complex Trip</name>
    <Folder>
        <name>Manhattan</name>
        <Placemark>
            <name>Empire State</name>
            <Point><coordinates>-73.9857,40.7484,0</coordinates></Point>
        </Placemark>
        <Placemark>
            <name>Central Park Area</name>
            <Polygon>
                <outerBoundaryIs>
                    <LinearRing>
                        <coordinates>-74.0,40.7 -73.9,40.7 -73.9,40.8 -74.0,40.8 -74.0,40.7</coordinates>
                    </LinearRing>
                </outerBoundaryIs>
            </Polygon>
        </Placemark>
        <Placemark>
            <name>walk</name>
            <LineString>
                <coordinates>-73.9857,40.7484,0 -73.9655,40.7829,0</coordinates>
            </LineString>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        Assert.Single(region.Areas);
        Assert.Single(trip.Segments);
    }

    [Fact]
    public void Parse_EmptyFolderName_DefaultsToUnnamedLayer()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name></name>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Regions);
        Assert.Equal("Unnamed layer", trip.Regions.First().Name);
    }

    [Fact]
    public void Parse_PlaceWithoutName_DefaultsToPlace()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        Assert.Equal("Place", region.Places.First().Name);
    }

    [Fact]
    public void Parse_NegativeCoordinates_ParsesCorrectly()
    {
        // Arrange - Sydney, Australia
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Sydney</name>
        <Placemark>
            <name>Opera House</name>
            <Point><coordinates>151.2153,-33.8568,0</coordinates></Point>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        var place = trip.Regions.First().Places.First();
        Assert.Equal(-33.8568, place.Location.Y, 4);
        Assert.Equal(151.2153, place.Location.X, 4);
    }

    [Fact]
    public void Parse_SegmentNearPlaces_LinksFromAndToPlaces()
    {
        // Arrange - Segment starts and ends very close to places
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Start Point</name>
            <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        </Placemark>
        <Placemark>
            <name>End Point</name>
            <Point><coordinates>-73.9855,40.7580,0</coordinates></Point>
        </Placemark>
        <Placemark>
            <name>drive</name>
            <LineString>
                <coordinates>-74.0060,40.7128,0 -73.9855,40.7580,0</coordinates>
            </LineString>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Single(trip.Segments);
        var segment = trip.Segments.First();
        var places = trip.Regions.First().Places.ToList();
        // Should be linked to the places since they're at exact same coordinates
        Assert.NotNull(segment.FromPlaceId);
        Assert.NotNull(segment.ToPlaceId);
        Assert.Equal(places[0].Id, segment.FromPlaceId);
        Assert.Equal(places[1].Id, segment.ToPlaceId);
    }

    [Fact]
    public void Parse_MissingDocumentName_DefaultsToImportedMyMaps()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        Assert.Equal("Imported My Maps", trip.Name);
    }

    [Fact]
    public void Parse_LineStringWithMultipleCoordinates_CreatesCompleteRoute()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>route</name>
            <LineString>
                <coordinates>-74.0060,40.7128,0 -73.9900,40.7300,0 -73.9855,40.7580,0 -73.9776,40.7614,0</coordinates>
            </LineString>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        var lineString = (LineString)trip.Segments.First().RouteGeometry;
        Assert.Equal(4, lineString.NumPoints);
    }

    [Fact]
    public void Parse_SetsUpdatedAtToUtcNow()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document><name>Trip</name></Document>
{KmlFooter}";
        using var stream = CreateStream(kml);
        var before = DateTime.UtcNow;

        // Act
        var trip = GoogleMyMapsKmlParser.Parse(stream, "user1");

        // Assert
        var after = DateTime.UtcNow;
        Assert.True(trip.UpdatedAt >= before && trip.UpdatedAt <= after);
    }
}
