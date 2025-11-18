using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the KmlLocationParser which parses Wayfarer KML export format.
/// </summary>
public class KmlLocationParserTests
{
    private readonly KmlLocationParser _parser;

    public KmlLocationParserTests()
    {
        _parser = new KmlLocationParser(NullLogger<KmlLocationParser>.Instance);
    }

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
    public async Task ParseAsync_EmptyKml_ReturnsEmptyList()
    {
        // Arrange
        var kml = $"{KmlHeader}<Document></Document>{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_SinglePlacemark_ParsesCorrectly()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <name>2024-01-15T10:30:00Z</name>
        <Point>
            <coordinates>-74.0060,40.7128,0</coordinates>
        </Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("user1", location.UserId);
        Assert.Equal(40.7128, location.Coordinates.Y, 4); // Latitude
        Assert.Equal(-74.0060, location.Coordinates.X, 4); // Longitude
    }

    [Fact]
    public async Task ParseAsync_PlacemarkWithAltitude_ParsesAltitudeFromCoordinates()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point>
            <coordinates>-74.0060,40.7128,125.5</coordinates>
        </Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(125.5, result[0].Altitude);
    }

    [Fact]
    public async Task ParseAsync_PlacemarkWithExtendedData_ParsesAllMetadata()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <description>Test notes</description>
        <Point>
            <coordinates>-0.1278,51.5074,25.3</coordinates>
        </Point>
        <ExtendedData>
            <Data name=""TimestampUtc""><value>2024-02-20T14:45:00Z</value></Data>
            <Data name=""LocalTimestamp""><value>2024-02-20T14:45:00Z</value></Data>
            <Data name=""TimeZoneId""><value>Europe/London</value></Data>
            <Data name=""Accuracy""><value>10.5</value></Data>
            <Data name=""Speed""><value>1.5</value></Data>
            <Data name=""Activity""><value>Walking</value></Data>
            <Data name=""Address""><value>123 Main St</value></Data>
            <Data name=""FullAddress""><value>123 Main Street London</value></Data>
            <Data name=""AddressNumber""><value>123</value></Data>
            <Data name=""StreetName""><value>Main Street</value></Data>
            <Data name=""PostCode""><value>SW1A 1AA</value></Data>
            <Data name=""Place""><value>Westminster</value></Data>
            <Data name=""Region""><value>London</value></Data>
            <Data name=""Country""><value>UK</value></Data>
        </ExtendedData>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(51.5074, location.Coordinates.Y, 4);
        Assert.Equal(-0.1278, location.Coordinates.X, 4);
        Assert.Equal(25.3, location.Altitude);
        Assert.Equal(10.5, location.Accuracy);
        Assert.Equal(1.5, location.Speed);
        Assert.Equal("Walking", location.ImportedActivityName);
        Assert.Equal("123 Main St", location.Address);
        Assert.Equal("123 Main Street London", location.FullAddress);
        Assert.Equal("123", location.AddressNumber);
        Assert.Equal("Main Street", location.StreetName);
        Assert.Equal("SW1A 1AA", location.PostCode);
        Assert.Equal("Westminster", location.Place);
        Assert.Equal("London", location.Region);
        Assert.Equal("UK", location.Country);
        Assert.Equal("Test notes", location.Notes);
        Assert.Equal("Europe/London", location.TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_MultiplePlacemarks_ParsesAll()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
    </Placemark>
    <Placemark>
        <Point><coordinates>-73.9855,40.7580,0</coordinates></Point>
    </Placemark>
    <Placemark>
        <Point><coordinates>-73.9776,40.7614,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_PlacemarkWithoutPoint_SkipsPlacemark()
    {
        // Arrange - Placemark with LineString instead of Point
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <LineString><coordinates>-74.0060,40.7128,0</coordinates></LineString>
    </Placemark>
    <Placemark>
        <Point><coordinates>-73.9855,40.7580,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only Point placemark
    }

    [Fact]
    public async Task ParseAsync_InvalidCoordinates_SkipsPlacemark()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>invalid</coordinates></Point>
    </Placemark>
    <Placemark>
        <Point><coordinates>-73.9855,40.7580,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid placemark
    }

    [Fact]
    public async Task ParseAsync_NegativeCoordinates_ParsesCorrectly()
    {
        // Arrange - Southern hemisphere
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>151.2093,-33.8688,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(-33.8688, result[0].Coordinates.Y, 4);
        Assert.Equal(151.2093, result[0].Coordinates.X, 4);
    }

    [Fact]
    public async Task ParseAsync_MissingTimezone_DefaultsToUtc()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("UTC", result[0].TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_AltitudeInExtendedData_OverridesCoordinateAltitude()
    {
        // Arrange - Altitude in ExtendedData should override coordinate altitude
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128,50.0</coordinates></Point>
        <ExtendedData>
            <Data name=""Altitude""><value>100.0</value></Data>
        </ExtendedData>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(100.0, result[0].Altitude); // ExtendedData value
    }

    [Fact]
    public async Task ParseAsync_AddressWithoutFullAddress_UsesAddressAsFallback()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        <ExtendedData>
            <Data name=""Address""><value>My Address</value></Data>
        </ExtendedData>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("My Address", result[0].Address);
        Assert.Equal("My Address", result[0].FullAddress);
    }

    [Fact]
    public async Task ParseAsync_NotesFromDescription_ParsesCorrectly()
    {
        // Arrange - Notes from description when not in ExtendedData
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <description>Description notes</description>
        <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("Description notes", result[0].Notes);
    }

    [Fact]
    public async Task ParseAsync_LargeDataset_HandlesEfficiently()
    {
        // Arrange
        var sb = new StringBuilder();
        sb.Append(KmlHeader);
        sb.Append("<Document>");
        for (int i = 0; i < 100; i++)
        {
            var lat = 40.0 + (i * 0.001);
            var lng = -74.0 + (i * 0.001);
            sb.Append($@"<Placemark><Point><coordinates>{lng},{lat},0</coordinates></Point></Placemark>");
        }
        sb.Append("</Document>");
        sb.Append(KmlFooter);
        using var stream = CreateStream(sb.ToString());

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(100, result.Count);
    }

    [Fact]
    public async Task ParseAsync_TimestampFromExtendedData_ParsesCorrectly()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        <ExtendedData>
            <Data name=""TimestampUtc""><value>2024-01-15T10:30:00Z</value></Data>
        </ExtendedData>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc), result[0].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_NestedFolders_ParsesAllPlacemarks()
    {
        // Arrange - Placemarks in nested folders
        var kml = $@"{KmlHeader}
<Document>
    <Folder>
        <Placemark>
            <Point><coordinates>-74.0060,40.7128,0</coordinates></Point>
        </Placemark>
        <Folder>
            <Placemark>
                <Point><coordinates>-73.9855,40.7580,0</coordinates></Point>
            </Placemark>
        </Folder>
    </Folder>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ParseAsync_CoordinatesWithOnlyTwoValues_ParsesWithoutAltitude()
    {
        // Arrange
        var kml = $@"{KmlHeader}
<Document>
    <Placemark>
        <Point><coordinates>-74.0060,40.7128</coordinates></Point>
    </Placemark>
</Document>
{KmlFooter}";
        using var stream = CreateStream(kml);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Null(result[0].Altitude);
    }
}
