using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the GpxLocationParser which parses Wayfarer GPX export format.
/// </summary>
public class GpxLocationParserTests
{
    private readonly GpxLocationParser _parser;

    public GpxLocationParserTests()
    {
        _parser = new GpxLocationParser(NullLogger<GpxLocationParser>.Instance);
    }

    /// <summary>
    /// Creates a memory stream from a GPX XML string for testing.
    /// </summary>
    private static MemoryStream CreateStream(string gpx)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(gpx));
    }

    private const string GpxHeader = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<gpx version=""1.1"" creator=""Wayfarer"" xmlns=""http://www.topografix.com/GPX/1/1"" xmlns:wayfarer=""https://wayfarer.app/schemas/gpx"">";
    private const string GpxFooter = @"</gpx>";

    [Fact]
    public async Task ParseAsync_EmptyGpx_ReturnsEmptyList()
    {
        // Arrange
        var gpx = $"{GpxHeader}{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_SingleTrackPoint_ParsesCorrectly()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00Z</time>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("user1", location.UserId);
        Assert.Equal(40.7128, location.Coordinates.Y, 4); // Latitude
        Assert.Equal(-74.0060, location.Coordinates.X, 4); // Longitude
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc), location.Timestamp);
    }

    [Fact]
    public async Task ParseAsync_TrackPointWithElevation_ParsesAltitude()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <ele>125.5</ele>
        <time>2024-01-15T10:30:00Z</time>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(125.5, result[0].Altitude);
    }

    [Fact]
    public async Task ParseAsync_TrackPointWithExtensions_ParsesAllMetadata()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""51.5074"" lon=""-0.1278"">
        <ele>25.3</ele>
        <time>2024-02-20T14:45:00Z</time>
        <extensions>
            <wayfarer:localTimestamp>2024-02-20T14:45:00Z</wayfarer:localTimestamp>
            <wayfarer:timeZoneId>Europe/London</wayfarer:timeZoneId>
            <wayfarer:accuracy>10.5</wayfarer:accuracy>
            <wayfarer:speed>1.5</wayfarer:speed>
            <wayfarer:activity>Walking</wayfarer:activity>
            <wayfarer:address>123 Main St</wayfarer:address>
            <wayfarer:fullAddress>123 Main Street London</wayfarer:fullAddress>
            <wayfarer:addressNumber>123</wayfarer:addressNumber>
            <wayfarer:streetName>Main Street</wayfarer:streetName>
            <wayfarer:postCode>SW1A 1AA</wayfarer:postCode>
            <wayfarer:place>Westminster</wayfarer:place>
            <wayfarer:region>London</wayfarer:region>
            <wayfarer:country>UK</wayfarer:country>
            <wayfarer:notes>Test notes</wayfarer:notes>
        </extensions>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

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
    public async Task ParseAsync_MultipleTrackPoints_ParsesAll()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060""><time>2024-01-15T10:00:00Z</time></trkpt>
    <trkpt lat=""40.7580"" lon=""-73.9855""><time>2024-01-15T11:00:00Z</time></trkpt>
    <trkpt lat=""40.7614"" lon=""-73.9776""><time>2024-01-15T12:00:00Z</time></trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_InvalidLatitude_SkipsTrackPoint()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""invalid"" lon=""-74.0060""><time>2024-01-15T10:00:00Z</time></trkpt>
    <trkpt lat=""40.7580"" lon=""-73.9855""><time>2024-01-15T11:00:00Z</time></trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid point
    }

    [Fact]
    public async Task ParseAsync_MissingLongitude_SkipsTrackPoint()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128""><time>2024-01-15T10:00:00Z</time></trkpt>
    <trkpt lat=""40.7580"" lon=""-73.9855""><time>2024-01-15T11:00:00Z</time></trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid point
    }

    [Fact]
    public async Task ParseAsync_NegativeCoordinates_ParsesCorrectly()
    {
        // Arrange - Southern hemisphere
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""-33.8688"" lon=""151.2093"">
        <time>2024-01-15T10:00:00Z</time>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

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
        // Arrange - No timezone extension
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00Z</time>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("UTC", result[0].TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_AddressWithoutFullAddress_UsesAddressAsFallback()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00Z</time>
        <extensions>
            <wayfarer:address>My Address</wayfarer:address>
        </extensions>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("My Address", result[0].Address);
        Assert.Equal("My Address", result[0].FullAddress);
    }

    [Fact]
    public async Task ParseAsync_MultipleTracks_ParsesAll()
    {
        // Arrange - Multiple track elements
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060""><time>2024-01-15T10:00:00Z</time></trkpt>
</trkseg></trk>
<trk><trkseg>
    <trkpt lat=""51.5074"" lon=""-0.1278""><time>2024-01-15T12:00:00Z</time></trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ParseAsync_LargeDataset_HandlesEfficiently()
    {
        // Arrange
        var sb = new StringBuilder();
        sb.Append(GpxHeader);
        sb.Append("<trk><trkseg>");
        for (int i = 0; i < 100; i++)
        {
            var lat = 40.0 + (i * 0.001);
            var lng = -74.0 + (i * 0.001);
            var time = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 5);
            sb.Append($@"<trkpt lat=""{lat}"" lon=""{lng}""><time>{time:yyyy-MM-ddTHH:mm:ssZ}</time></trkpt>");
        }
        sb.Append("</trkseg></trk>");
        sb.Append(GpxFooter);
        using var stream = CreateStream(sb.ToString());

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(100, result.Count);
    }

    [Fact]
    public async Task ParseAsync_TimestampWithOffset_ConvertsToUtc()
    {
        // Arrange
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00-05:00</time>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        // -05:00 offset means UTC should be 15:30
        Assert.Equal(new DateTime(2024, 1, 15, 15, 30, 0, DateTimeKind.Utc), result[0].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_PartialExtensions_ParsesAvailableFields()
    {
        // Arrange - Only some extension fields
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00Z</time>
        <extensions>
            <wayfarer:accuracy>15.5</wayfarer:accuracy>
            <wayfarer:activity>Running</wayfarer:activity>
        </extensions>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(15.5, location.Accuracy);
        Assert.Equal("Running", location.ImportedActivityName);
        Assert.Null(location.Speed);
        Assert.Null(location.Address);
    }

    [Fact]
    public async Task ParseAsync_NoRootElement_ThrowsFormatException()
    {
        // Arrange - Invalid XML
        var gpx = "";
        using var stream = CreateStream(gpx);

        // Act & Assert
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => _parser.ParseAsync(stream, "user1"));
    }

    [Fact]
    public async Task ParseAsync_MetadataFields_ParsesAllIncludingSource()
    {
        // Arrange - Include all metadata fields including Source for roundtrip support
        var gpx = $@"{GpxHeader}
<trk><trkseg>
    <trkpt lat=""40.7128"" lon=""-74.0060"">
        <time>2024-01-15T10:30:00Z</time>
        <extensions>
            <wayfarer:source>mobile-log</wayfarer:source>
            <wayfarer:isUserInvoked>true</wayfarer:isUserInvoked>
            <wayfarer:provider>gps</wayfarer:provider>
            <wayfarer:bearing>180.5</wayfarer:bearing>
            <wayfarer:appVersion>1.2.3</wayfarer:appVersion>
            <wayfarer:appBuild>45</wayfarer:appBuild>
            <wayfarer:deviceModel>Pixel 7 Pro</wayfarer:deviceModel>
            <wayfarer:osVersion>Android 14</wayfarer:osVersion>
            <wayfarer:batteryLevel>85</wayfarer:batteryLevel>
            <wayfarer:isCharging>false</wayfarer:isCharging>
        </extensions>
    </trkpt>
</trkseg></trk>
{GpxFooter}";
        using var stream = CreateStream(gpx);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("mobile-log", location.Source);
        Assert.True(location.IsUserInvoked);
        Assert.Equal("gps", location.Provider);
        Assert.Equal(180.5, location.Bearing);
        Assert.Equal("1.2.3", location.AppVersion);
        Assert.Equal("45", location.AppBuild);
        Assert.Equal("Pixel 7 Pro", location.DeviceModel);
        Assert.Equal("Android 14", location.OsVersion);
        Assert.Equal(85, location.BatteryLevel);
        Assert.False(location.IsCharging);
    }
}
