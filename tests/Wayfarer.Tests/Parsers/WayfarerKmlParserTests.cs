using System.Text;
using NetTopologySuite.Geometries;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the WayfarerKmlParser which parses Wayfarer-Extended-KML format into Trip objects.
/// </summary>
public class WayfarerKmlParserTests
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
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.NotNull(trip);
        Assert.Equal("Test Trip", trip.Name);
        Assert.False(trip.IsPublic);
        Assert.NotEqual(Guid.Empty, trip.Id);
    }

    [Fact]
    public void Parse_TripWithExtendedData_ParsesAllMetadata()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var kml = $@"{KmlHeader}
<Document>
    <name>Detailed Trip</name>
    <ExtendedData>
        <Data name=""TripId""><value>{tripId}</value></Data>
        <Data name=""CoverImageUrl""><value>https://example.com/cover.jpg</value></Data>
        <Data name=""NotesHtml""><value>&lt;p&gt;Trip notes&lt;/p&gt;</value></Data>
        <Data name=""CenterLat""><value>40.7128</value></Data>
        <Data name=""CenterLon""><value>-74.0060</value></Data>
        <Data name=""Zoom""><value>12</value></Data>
    </ExtendedData>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Equal(tripId, trip.Id);
        Assert.Equal("https://example.com/cover.jpg", trip.CoverImageUrl);
        Assert.Equal("<p>Trip notes</p>", trip.Notes);
        Assert.Equal(40.7128, trip.CenterLat);
        Assert.Equal(-74.0060, trip.CenterLon);
        Assert.Equal(12, trip.Zoom);
    }

    [Fact]
    public void Parse_TripWithTags_ParsesTagsCorrectly()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Tagged Trip</name>
    <ExtendedData>
        <Data name=""Tags""><value>adventure,beach,road-trip</value></Data>
    </ExtendedData>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.NotNull(trip.Tags);
        Assert.Equal(3, trip.Tags.Count);
        Assert.Contains(trip.Tags, t => t.Slug == "adventure");
        Assert.Contains(trip.Tags, t => t.Slug == "beach");
        Assert.Contains(trip.Tags, t => t.Slug == "road-trip");
    }

    [Fact]
    public void Parse_RegionFolder_CreatesRegionWithMetadata()
    {
        // Arrange
        var regionId = Guid.NewGuid();
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Manhattan</name>
        <ExtendedData>
            <Data name=""RegionId""><value>{regionId}</value></Data>
            <Data name=""DisplayOrder""><value>1</value></Data>
            <Data name=""NotesHtml""><value>Region notes</value></Data>
            <Data name=""CenterLat""><value>40.7831</value></Data>
            <Data name=""CenterLon""><value>-73.9712</value></Data>
        </ExtendedData>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Equal(regionId, region.Id);
        Assert.Equal("Manhattan", region.Name);
        Assert.Equal(1, region.DisplayOrder);
        Assert.Equal("Region notes", region.Notes);
        Assert.NotNull(region.Center);
        Assert.Equal(40.7831, region.Center.Y, 4);
        Assert.Equal(-73.9712, region.Center.X, 4);
    }

    [Fact]
    public void Parse_PlacemarkInFolder_CreatesPlaceWithAllMetadata()
    {
        // Arrange
        var placeId = Guid.NewGuid();
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Times Square</name>
            <Point>
                <coordinates>-73.9855,40.7580,0</coordinates>
            </Point>
            <ExtendedData>
                <Data name=""PlaceId""><value>{placeId}</value></Data>
                <Data name=""DisplayOrder""><value>2</value></Data>
                <Data name=""NotesHtml""><value>Place notes</value></Data>
                <Data name=""IconName""><value>landmark</value></Data>
                <Data name=""MarkerColor""><value>blue</value></Data>
                <Data name=""Address""><value>Times Square, NYC</value></Data>
            </ExtendedData>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        var place = region.Places.First();
        Assert.Equal(placeId, place.Id);
        Assert.Equal("Times Square", place.Name);
        Assert.Equal(2, place.DisplayOrder);
        Assert.Equal("Place notes", place.Notes);
        Assert.Equal("landmark", place.IconName);
        Assert.Equal("blue", place.MarkerColor);
        Assert.Equal("Times Square, NYC", place.Address);
        Assert.Equal(40.7580, place.Location!.Y, 4);
        Assert.Equal(-73.9855, place.Location!.X, 4);
    }

    [Fact]
    public void Parse_AreaInFolder_CreatesAreaWithMetadata()
    {
        // Arrange
        var areaId = Guid.NewGuid();
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>Central Park</name>
            <Polygon>
                <outerBoundaryIs>
                    <LinearRing>
                        <coordinates>-74.0,40.7 -73.9,40.7 -73.9,40.8 -74.0,40.8 -74.0,40.7</coordinates>
                    </LinearRing>
                </outerBoundaryIs>
            </Polygon>
            <ExtendedData>
                <Data name=""AreaId""><value>{areaId}</value></Data>
                <Data name=""DisplayOrder""><value>3</value></Data>
                <Data name=""FillHex""><value>#00FF00</value></Data>
                <Data name=""NotesHtml""><value>Area notes</value></Data>
            </ExtendedData>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var region = trip.Regions.First();
        Assert.Single(region.Areas);
        var area = region.Areas.First();
        Assert.Equal(areaId, area.Id);
        Assert.Equal("Central Park", area.Name);
        Assert.Equal(3, area.DisplayOrder);
        Assert.Equal("#00FF00", area.FillHex);
        Assert.Equal("Area notes", area.Notes);
        Assert.IsType<Polygon>(area.Geometry);
    }

    [Fact]
    public void Parse_SegmentsFolder_CreatesSegmentsWithMetadata()
    {
        // Arrange
        var segmentId = Guid.NewGuid();
        var fromPlaceId = Guid.NewGuid();
        var toPlaceId = Guid.NewGuid();
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Segments</name>
        <Placemark>
            <LineString>
                <coordinates>-74.0060,40.7128,0 -73.9855,40.7580,0</coordinates>
            </LineString>
            <ExtendedData>
                <Data name=""SegmentId""><value>{segmentId}</value></Data>
                <Data name=""FromPlaceId""><value>{fromPlaceId}</value></Data>
                <Data name=""ToPlaceId""><value>{toPlaceId}</value></Data>
                <Data name=""Mode""><value>driving</value></Data>
                <Data name=""DistanceKm""><value>5.5</value></Data>
                <Data name=""DurationMin""><value>15</value></Data>
                <Data name=""DisplayOrder""><value>1</value></Data>
                <Data name=""NotesHtml""><value>Segment notes</value></Data>
            </ExtendedData>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Single(trip.Segments);
        var segment = trip.Segments.First();
        Assert.Equal(segmentId, segment.Id);
        Assert.Equal(fromPlaceId, segment.FromPlaceId);
        Assert.Equal(toPlaceId, segment.ToPlaceId);
        Assert.Equal("driving", segment.Mode);
        Assert.Equal(5.5, segment.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromMinutes(15), segment.EstimatedDuration);
        Assert.Equal(1, segment.DisplayOrder);
        Assert.Equal("Segment notes", segment.Notes);
        Assert.IsType<LineString>(segment.RouteGeometry);
    }

    [Fact]
    public void Parse_MultipleRegions_CreatesAllRegions()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Multi-region Trip</name>
    <Folder>
        <name>Region 1</name>
        <ExtendedData>
            <Data name=""DisplayOrder""><value>0</value></Data>
        </ExtendedData>
    </Folder>
    <Folder>
        <name>Region 2</name>
        <ExtendedData>
            <Data name=""DisplayOrder""><value>1</value></Data>
        </ExtendedData>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Equal(2, trip.Regions.Count);
        var regionNames = trip.Regions.Select(r => r.Name).ToList();
        Assert.Contains("Region 1", regionNames);
        Assert.Contains("Region 2", regionNames);
    }

    [Fact]
    public void Parse_SegmentsFolderSkippedForRegions()
    {
        // Arrange - Segments folder should not be treated as a region
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Regular Region</name>
    </Folder>
    <Folder>
        <name>Segments</name>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Single(trip.Regions);
        Assert.Equal("Regular Region", trip.Regions.First().Name);
    }

    [Fact]
    public void Parse_PlacemarkWithoutCoordinates_SkipsPlace()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Trip</name>
    <Folder>
        <name>Region</name>
        <Placemark>
            <name>No Coordinates</name>
            <Point></Point>
        </Placemark>
        <Placemark>
            <name>With Coordinates</name>
            <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        Assert.Equal("With Coordinates", region.Places.First().Name);
    }

    [Fact]
    public void Parse_MissingKmlRoot_ThrowsFormatException()
    {
        // Arrange
        var kml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<invalid></invalid>";
        using var stream = CreateStream(kml);

        // Act & Assert
        Assert.Throws<FormatException>(() => WayfarerKmlParser.Parse(stream));
    }

    [Fact]
    public void Parse_MissingDocument_ThrowsFormatException()
    {
        // Arrange
        var kml = $@"{KmlHeader}
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act & Assert
        Assert.Throws<FormatException>(() => WayfarerKmlParser.Parse(stream));
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
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var place = trip.Regions.First().Places.First();
        Assert.Equal(-33.8568, place.Location!.Y, 4);
        Assert.Equal(151.2153, place.Location!.X, 4);
    }

    [Fact]
    public void Parse_MissingDocumentName_DefaultsToImportedTrip()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Equal("Imported trip", trip.Name);
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
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var region = trip.Regions.First();
        Assert.Equal("Place", region.Places.First().Name);
    }

    [Fact]
    public void Parse_ComplexTrip_ParsesAllElements()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <name>Complex Trip</name>
    <ExtendedData>
        <Data name=""Tags""><value>adventure</value></Data>
        <Data name=""CenterLat""><value>40.7128</value></Data>
        <Data name=""CenterLon""><value>-74.0060</value></Data>
    </ExtendedData>
    <Folder>
        <name>Manhattan</name>
        <Placemark>
            <name>Empire State</name>
            <Point><coordinates>-73.9857,40.7484,0</coordinates></Point>
        </Placemark>
        <Placemark>
            <name>Park Area</name>
            <Polygon>
                <outerBoundaryIs>
                    <LinearRing>
                        <coordinates>-74.0,40.7 -73.9,40.7 -73.9,40.8 -74.0,40.8 -74.0,40.7</coordinates>
                    </LinearRing>
                </outerBoundaryIs>
            </Polygon>
        </Placemark>
    </Folder>
    <Folder>
        <name>Segments</name>
        <Placemark>
            <LineString>
                <coordinates>-74.0060,40.7128,0 -73.9855,40.7580,0</coordinates>
            </LineString>
            <ExtendedData>
                <Data name=""Mode""><value>walking</value></Data>
            </ExtendedData>
        </Placemark>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.Equal("Complex Trip", trip.Name);
        Assert.Single(trip.Tags);
        Assert.Equal(40.7128, trip.CenterLat);
        Assert.Equal(-74.0060, trip.CenterLon);
        Assert.Single(trip.Regions);
        var region = trip.Regions.First();
        Assert.Single(region.Places);
        Assert.Single(region.Areas);
        Assert.Single(trip.Segments);
        Assert.Equal("walking", trip.Segments.First().Mode);
    }

    [Fact]
    public void Parse_SetsIsPublicToFalse()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document><name>Trip</name></Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        Assert.False(trip.IsPublic);
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
        var trip = WayfarerKmlParser.Parse(stream);

        // Assert
        var after = DateTime.UtcNow;
        Assert.True(trip.UpdatedAt >= before && trip.UpdatedAt <= after);
    }
}
