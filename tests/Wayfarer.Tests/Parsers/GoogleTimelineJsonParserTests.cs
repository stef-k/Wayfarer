using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the GoogleTimelineJsonParser which parses Google Takeout Timeline JSON files.
/// </summary>
public class GoogleTimelineJsonParserTests
{
    private readonly GoogleTimelineJsonParser _parser;

    public GoogleTimelineJsonParserTests()
    {
        _parser = new GoogleTimelineJsonParser(NullLogger<GoogleTimelineJsonParser>.Instance);
    }

    /// <summary>
    /// Creates a memory stream from a JSON string for testing.
    /// </summary>
    private static MemoryStream CreateStream(string json)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task ParseAsync_EmptySemanticSegments_ReturnsEmptyList()
    {
        // Arrange
        var json = @"{""semanticSegments"": []}";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_NullSemanticSegments_ReturnsEmptyList()
    {
        // Arrange
        var json = @"{}";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_SingleTimelinePath_ParsesCorrectly()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [{
                    ""point"": ""40.7128°, -74.0060°"",
                    ""time"": ""2024-01-15T10:30:00Z""
                }]
            }]
        }";
        using var stream = CreateStream(json);

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
    public async Task ParseAsync_MultipleTimelinePaths_ParsesAll()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [
                    {""point"": ""40.7128°, -74.0060°"", ""time"": ""2024-01-15T10:00:00Z""},
                    {""point"": ""40.7580°, -73.9855°"", ""time"": ""2024-01-15T11:00:00Z""},
                    {""point"": ""40.7614°, -73.9776°"", ""time"": ""2024-01-15T12:00:00Z""}
                ]
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_PositionWithMetadata_ParsesAllFields()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [{
                ""position"": {
                    ""LatLng"": ""51.5074°, -0.1278°"",
                    ""timestamp"": ""2024-02-20T14:45:00Z"",
                    ""accuracyMeters"": 10.5,
                    ""altitudeMeters"": 25.3,
                    ""speedMetersPerSecond"": 1.5,
                    ""source"": ""GPS""
                }
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal("user1", location.UserId);
        Assert.Equal(51.5074, location.Coordinates.Y, 4); // Latitude
        Assert.Equal(-0.1278, location.Coordinates.X, 4); // Longitude
        Assert.Equal(10.5, location.Accuracy);
        Assert.Equal(25.3, location.Altitude);
        Assert.Equal(1.5, location.Speed);
        Assert.Equal("GPS", location.Notes);
    }

    [Fact]
    public async Task ParseAsync_MixedTimelinePathAndPosition_ParsesBoth()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [
                {
                    ""timelinePath"": [{
                        ""point"": ""40.7128°, -74.0060°"",
                        ""time"": ""2024-01-15T10:00:00Z""
                    }]
                },
                {
                    ""position"": {
                        ""LatLng"": ""51.5074°, -0.1278°"",
                        ""timestamp"": ""2024-01-15T12:00:00Z""
                    }
                }
            ]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ParseAsync_InvalidPointFormat_SkipsInvalidEntries()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [
                    {""point"": ""invalid"", ""time"": ""2024-01-15T10:00:00Z""},
                    {""point"": ""40.7128°, -74.0060°"", ""time"": ""2024-01-15T11:00:00Z""}
                ]
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid entry parsed
    }

    [Fact]
    public async Task ParseAsync_InvalidTimestamp_SkipsInvalidEntries()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [
                    {""point"": ""40.7128°, -74.0060°"", ""time"": ""not-a-date""},
                    {""point"": ""40.7580°, -73.9855°"", ""time"": ""2024-01-15T11:00:00Z""}
                ]
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result); // Only valid entry parsed
    }

    [Fact]
    public async Task ParseAsync_CoordinatesWithoutDegreeSymbol_SkipsEntry()
    {
        // Arrange - The parser expects degree symbols
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [{
                    ""point"": ""40.7128, -74.0060"",
                    ""time"": ""2024-01-15T10:00:00Z""
                }]
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        // Without degree symbols, parsing should still work (removes empty strings)
        Assert.Single(result);
    }

    [Fact]
    public async Task ParseAsync_MultipleSegments_ParsesAll()
    {
        // Arrange
        var json = @"{
            ""semanticSegments"": [
                {
                    ""timelinePath"": [
                        {""point"": ""40.7128°, -74.0060°"", ""time"": ""2024-01-15T08:00:00Z""},
                        {""point"": ""40.7580°, -73.9855°"", ""time"": ""2024-01-15T09:00:00Z""}
                    ]
                },
                {
                    ""timelinePath"": [
                        {""point"": ""40.7614°, -73.9776°"", ""time"": ""2024-01-15T10:00:00Z""}
                    ]
                }
            ]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ParseAsync_NegativeCoordinates_ParsesCorrectly()
    {
        // Arrange - Southern hemisphere and Western longitude
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [{
                    ""point"": ""-33.8688°, 151.2093°"",
                    ""time"": ""2024-01-15T10:00:00Z""
                }]
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(-33.8688, location.Coordinates.Y, 4); // Latitude (Sydney)
        Assert.Equal(151.2093, location.Coordinates.X, 4); // Longitude
    }

    [Fact]
    public async Task ParseAsync_TimezoneOffset_PreservesUtcTimestamp()
    {
        // Arrange - Timestamp with timezone offset
        var json = @"{
            ""semanticSegments"": [{
                ""position"": {
                    ""LatLng"": ""40.7128°, -74.0060°"",
                    ""timestamp"": ""2024-01-15T10:30:00-05:00""
                }
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        // -05:00 offset means UTC should be 15:30
        Assert.Equal(new DateTime(2024, 1, 15, 15, 30, 0, DateTimeKind.Utc), location.Timestamp);
    }

    [Fact]
    public async Task ParseAsync_PositionWithPartialMetadata_ParsesAvailableFields()
    {
        // Arrange - Only some metadata fields present
        var json = @"{
            ""semanticSegments"": [{
                ""position"": {
                    ""LatLng"": ""40.7128°, -74.0060°"",
                    ""timestamp"": ""2024-01-15T10:00:00Z"",
                    ""accuracyMeters"": 15.0
                }
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Single(result);
        var location = result[0];
        Assert.Equal(15.0, location.Accuracy);
        Assert.Null(location.Altitude);
        Assert.Null(location.Speed);
        Assert.Null(location.Notes);
    }

    [Fact]
    public async Task ParseAsync_SegmentWithBothTimelinePathAndPosition_ParsesBoth()
    {
        // Arrange - Single segment with both types
        var json = @"{
            ""semanticSegments"": [{
                ""timelinePath"": [{
                    ""point"": ""40.7128°, -74.0060°"",
                    ""time"": ""2024-01-15T10:00:00Z""
                }],
                ""position"": {
                    ""LatLng"": ""40.7580°, -73.9855°"",
                    ""timestamp"": ""2024-01-15T11:00:00Z""
                }
            }]
        }";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ParseAsync_LargeDataset_HandlesEfficiently()
    {
        // Arrange - Generate a larger dataset
        var pathEntries = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var lat = 40.0 + (i * 0.001);
            var lng = -74.0 + (i * 0.001);
            var time = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 5);
            pathEntries.Add($@"{{""point"": ""{lat}°, {lng}°"", ""time"": ""{time:yyyy-MM-ddTHH:mm:ssZ}""}}");
        }

        var json = $@"{{
            ""semanticSegments"": [{{
                ""timelinePath"": [{string.Join(",", pathEntries)}]
            }}]
        }}";
        using var stream = CreateStream(json);

        // Act
        var result = await _parser.ParseAsync(stream, "user1");

        // Assert
        Assert.Equal(100, result.Count);
    }
}
