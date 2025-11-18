using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the CsvLocationParser which parses Wayfarer CSV export format.
/// </summary>
public class CsvLocationParserTests
{
    private readonly CsvLocationParser _parser;

    public CsvLocationParserTests()
    {
        _parser = new CsvLocationParser(NullLogger<CsvLocationParser>.Instance);
    }

    /// <summary>
    /// Creates a memory stream from a CSV string for testing.
    /// </summary>
    private static MemoryStream CreateStream(string csv)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(csv));
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_ReturnsEmptyList()
    {
        // Arrange
        var csv = "";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_HeaderOnly_ReturnsEmptyList()
    {
        // Arrange
        var csv = "Latitude,Longitude,TimestampUtc,TimeZoneId";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_SingleRow_ParsesCorrectly()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,-74.0060,2024-01-15T10:30:00Z,America/New_York";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("user1", location.UserId);
        Assert.Equal(40.7128, location.Coordinates.Y, 4); // Latitude
        Assert.Equal(-74.0060, location.Coordinates.X, 4); // Longitude
        Assert.Equal("America/New_York", location.TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_MultipleRows_ParsesAll()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC
40.7580,-73.9855,2024-01-15T11:00:00Z,UTC
40.7614,-73.9776,2024-01-15T12:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_AllFields_ParsesAllMetadata()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,Accuracy,Altitude,Speed,Activity,Address,FullAddress,AddressNumber,StreetName,PostCode,Place,Region,Country,Notes
51.5074,-0.1278,2024-02-20T14:45:00Z,2024-02-20T14:45:00Z,Europe/London,10.5,25.3,1.5,Walking,123 Main St,123 Main Street London,123,Main Street,SW1A 1AA,Westminster,London,UK,Test notes";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(51.5074, location.Coordinates.Y, 4);
        Assert.Equal(-0.1278, location.Coordinates.X, 4);
        Assert.Equal(10.5, location.Accuracy);
        Assert.Equal(25.3, location.Altitude);
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
    public async Task ParseAsync_MissingLatitude_SkipsRow()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
,-74.0060,2024-01-15T10:00:00Z,UTC
40.7580,-73.9855,2024-01-15T11:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid row
        Assert.Equal(40.7580, result[0].Coordinates.Y, 4);
    }

    [Fact]
    public async Task ParseAsync_MissingLongitude_SkipsRow()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,,2024-01-15T10:00:00Z,UTC
40.7580,-73.9855,2024-01-15T11:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid row
    }

    [Fact]
    public async Task ParseAsync_InvalidCoordinates_SkipsRow()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
not-a-number,-74.0060,2024-01-15T10:00:00Z,UTC
40.7580,-73.9855,2024-01-15T11:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid row
    }

    [Fact]
    public async Task ParseAsync_MissingTimezone_DefaultsToUtc()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,-74.0060,2024-01-15T10:30:00Z,";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("UTC", result[0].TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_NegativeCoordinates_ParsesCorrectly()
    {
        // Arrange - Southern hemisphere
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
-33.8688,151.2093,2024-01-15T10:00:00Z,Australia/Sydney";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(-33.8688, result[0].Coordinates.Y, 4);
        Assert.Equal(151.2093, result[0].Coordinates.X, 4);
    }

    [Fact]
    public async Task ParseAsync_OptionalFieldsMissing_ParsesRequiredFields()
    {
        // Arrange - Only required fields
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Null(location.Accuracy);
        Assert.Null(location.Altitude);
        Assert.Null(location.Speed);
        Assert.Null(location.Address);
        Assert.Null(location.Notes);
    }

    [Fact]
    public async Task ParseAsync_WhitespaceInHeaders_TrimsHeaders()
    {
        // Arrange - Headers with extra whitespace
        var csv = @" Latitude , Longitude , TimestampUtc , TimeZoneId
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task ParseAsync_PartialMetadata_ParsesAvailableFields()
    {
        // Arrange - Some optional fields present
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId,Accuracy,Address
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC,15.5,Times Square";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(15.5, location.Accuracy);
        Assert.Equal("Times Square", location.Address);
        Assert.Null(location.Altitude);
    }

    [Fact]
    public async Task ParseAsync_AddressWithoutFullAddress_UsesAddressAsFallback()
    {
        // Arrange
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId,Address
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC,My Address";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal("My Address", result[0].Address);
        Assert.Equal("My Address", result[0].FullAddress);
    }

    [Fact]
    public async Task ParseAsync_LargeDataset_HandlesEfficiently()
    {
        // Arrange
        var sb = new StringBuilder();
        sb.AppendLine("Latitude,Longitude,TimestampUtc,TimeZoneId");
        for (int i = 0; i < 100; i++)
        {
            var lat = 40.0 + (i * 0.001);
            var lng = -74.0 + (i * 0.001);
            var time = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 5);
            sb.AppendLine($"{lat},{lng},{time:yyyy-MM-ddTHH:mm:ssZ},UTC");
        }
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
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId
40.7128,-74.0060,2024-01-15T10:30:00-05:00,America/New_York";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        // -05:00 offset means UTC should be 15:30
        Assert.Equal(new DateTime(2024, 1, 15, 15, 30, 0, DateTimeKind.Utc), result[0].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_DecimalWithDifferentFormats_ParsesCorrectly()
    {
        // Arrange - Using decimal points (invariant culture)
        var csv = @"Latitude,Longitude,TimestampUtc,TimeZoneId,Accuracy
40.7128,-74.0060,2024-01-15T10:00:00Z,UTC,15.75";
        using var stream = CreateStream(csv);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        Assert.Equal(15.75, result[0].Accuracy);
    }
}
