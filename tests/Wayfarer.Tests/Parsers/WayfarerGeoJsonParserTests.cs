using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for <see cref="WayfarerGeoJsonParser"/> covering GeoJSON import functionality.
/// </summary>
public class WayfarerGeoJsonParserTests
{
    private readonly WayfarerGeoJsonParser _parser;

    public WayfarerGeoJsonParserTests()
    {
        _parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
    }

    /// <summary>
    /// Converts a GeoJSON string to a MemoryStream.
    /// </summary>
    private static MemoryStream ToStream(string geoJson)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(geoJson));
    }

    #region Basic Parsing Tests

    [Fact]
    public async Task ParseAsync_ReturnsEmptyList_ForEmptyFeatureCollection()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": []
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_ParsesSinglePointFeature()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": {
                        ""type"": ""Point"",
                        ""coordinates"": [-74.006, 40.7128]
                    },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""TimeZoneId"": ""America/New_York""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("user-123", location.UserId);
        Assert.Equal(-74.006, location.Coordinates.X);
        Assert.Equal(40.7128, location.Coordinates.Y);
        Assert.Equal("America/New_York", location.TimeZoneId);
    }

    [Fact]
    public async Task ParseAsync_ParsesMultipleFeatures()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T10:00:00Z"" }
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-73.985, 40.758] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T11:00:00Z"" }
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-118.2437, 34.0522] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T12:00:00Z"" }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_SkipsNonPointGeometries()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T10:00:00Z"" }
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": {
                        ""type"": ""LineString"",
                        ""coordinates"": [[-74.006, 40.7128], [-73.985, 40.758]]
                    },
                    ""properties"": {}
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": {
                        ""type"": ""Polygon"",
                        ""coordinates"": [[[-74.0, 40.7], [-74.0, 40.8], [-73.9, 40.8], [-73.9, 40.7], [-74.0, 40.7]]]
                    },
                    ""properties"": {}
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal(-74.006, result[0].Coordinates.X);
    }

    #endregion

    #region Attribute Parsing Tests

    [Fact]
    public async Task ParseAsync_ParsesAllAttributes()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""LocalTimestamp"": ""2024-01-15T05:30:00"",
                        ""TimeZoneId"": ""America/New_York"",
                        ""Accuracy"": 10.5,
                        ""Altitude"": 25.3,
                        ""Speed"": 5.2,
                        ""Activity"": ""walking"",
                        ""Address"": ""123 Main St"",
                        ""FullAddress"": ""123 Main St, New York, NY 10001"",
                        ""AddressNumber"": ""123"",
                        ""StreetName"": ""Main St"",
                        ""PostCode"": ""10001"",
                        ""Place"": ""New York"",
                        ""Region"": ""New York"",
                        ""Country"": ""United States"",
                        ""Notes"": ""Test location""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        var location = result[0];

        Assert.Equal("America/New_York", location.TimeZoneId);
        Assert.Equal(10.5, location.Accuracy);
        Assert.Equal(25.3, location.Altitude);
        Assert.Equal(5.2, location.Speed);
        Assert.Equal("123 Main St", location.Address);
        Assert.Equal("123 Main St, New York, NY 10001", location.FullAddress);
        Assert.Equal("123", location.AddressNumber);
        Assert.Equal("Main St", location.StreetName);
        Assert.Equal("10001", location.PostCode);
        Assert.Equal("New York", location.Place);
        Assert.Equal("New York", location.Region);
        Assert.Equal("United States", location.Country);
        Assert.Equal("Test location", location.Notes);
        Assert.Equal("walking", location.ImportedActivityName);
    }

    [Fact]
    public async Task ParseAsync_HandlesAlternateAttributeNames()
    {
        // Arrange - uses "Street" instead of "StreetName", "Postcode" instead of "PostCode"
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""Street"": ""Broadway"",
                        ""Postcode"": ""10007""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal("Broadway", result[0].StreetName);
        Assert.Equal("10007", result[0].PostCode);
    }

    [Fact]
    public async Task ParseAsync_UsesAddressAsFullAddressFallback()
    {
        // Arrange - no FullAddress provided
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""Address"": ""456 Oak Ave""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal("456 Oak Ave", result[0].Address);
        Assert.Equal("456 Oak Ave", result[0].FullAddress);
    }

    [Fact]
    public async Task ParseAsync_HandlesMissingOptionalAttributes()
    {
        // Arrange - minimal properties
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        var location = result[0];

        Assert.Equal("UTC", location.TimeZoneId); // default
        Assert.Null(location.Accuracy);
        Assert.Null(location.Altitude);
        Assert.Null(location.Speed);
        Assert.Null(location.Address);
        Assert.Null(location.StreetName);
        Assert.Null(location.PostCode);
        Assert.Null(location.Place);
        Assert.Null(location.Region);
        Assert.Null(location.Country);
        Assert.Null(location.Notes);
    }

    #endregion

    #region Timestamp Parsing Tests

    [Fact]
    public async Task ParseAsync_ParsesIso8601TimestampWithZ()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-06-15T14:30:00Z""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        // Verify date and time components - parser returns UTC timestamp
        Assert.Equal(2024, result[0].Timestamp.Year);
        Assert.Equal(6, result[0].Timestamp.Month);
        Assert.Equal(15, result[0].Timestamp.Day);
        Assert.Equal(DateTimeKind.Utc, result[0].Timestamp.Kind);
    }

    [Fact]
    public async Task ParseAsync_ParsesIso8601TimestampWithOffset()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-06-15T10:30:00-04:00""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        // -04:00 offset means UTC time is +4 hours
        Assert.Equal(new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc), result[0].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_ParsesLocalTimestamp()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-06-15T14:30:00Z"",
                        ""LocalTimestamp"": ""2024-06-15T10:30:00""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal(new DateTime(2024, 6, 15, 10, 30, 0), result[0].LocalTimestamp);
    }

    [Fact]
    public async Task ParseAsync_UsesUtcAsFallbackForMissingLocalTimestamp()
    {
        // Arrange - no LocalTimestamp
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-06-15T14:30:00Z""
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal(result[0].Timestamp, result[0].LocalTimestamp);
    }

    [Fact]
    public async Task ParseAsync_UsesCurrentTimeForMissingTimestamp()
    {
        // Arrange - no timestamp at all
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {}
                }
            ]
        }";

        // Act
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.Single(result);
        Assert.True(result[0].Timestamp >= before && result[0].Timestamp <= after);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_HandlesNullPropertyValues()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""Address"": null,
                        ""Accuracy"": null,
                        ""Notes"": null
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Null(result[0].Address);
        Assert.Null(result[0].Accuracy);
        Assert.Null(result[0].Notes);
    }

    [Fact]
    public async Task ParseAsync_HandlesEmptyStringValues()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""Activity"": """"
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        // Empty activity should result in null ImportedActivityName
        Assert.Null(result[0].ImportedActivityName);
    }

    [Fact]
    public async Task ParseAsync_SetsUserIdOnAllLocations()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T10:00:00Z"" }
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-73.985, 40.758] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T11:00:00Z"" }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "specific-user-id");

        // Assert
        Assert.All(result, loc => Assert.Equal("specific-user-id", loc.UserId));
    }

    [Fact]
    public async Task ParseAsync_HandlesHighPrecisionCoordinates()
    {
        // Arrange
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.00597123456789, 40.71280987654321] },
                    ""properties"": { ""TimestampUtc"": ""2024-01-15T10:30:00Z"" }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Single(result);
        Assert.Equal(-74.00597123456789, result[0].Coordinates.X, 10);
        Assert.Equal(40.71280987654321, result[0].Coordinates.Y, 10);
    }

    [Fact]
    public async Task ParseAsync_HandlesNumericAccuracyValues()
    {
        // Arrange - integer and decimal accuracy
        var geoJson = @"{
            ""type"": ""FeatureCollection"",
            ""features"": [
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-74.006, 40.7128] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T10:30:00Z"",
                        ""Accuracy"": 15
                    }
                },
                {
                    ""type"": ""Feature"",
                    ""geometry"": { ""type"": ""Point"", ""coordinates"": [-73.985, 40.758] },
                    ""properties"": {
                        ""TimestampUtc"": ""2024-01-15T11:00:00Z"",
                        ""Accuracy"": 8.5
                    }
                }
            ]
        }";

        // Act
        var result = await _parser.ParseAsync(ToStream(geoJson), "user-123");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(15.0, result[0].Accuracy);
        Assert.Equal(8.5, result[1].Accuracy);
    }

    #endregion
}
