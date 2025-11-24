using System.Text.Json;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for <see cref="RoutablePoint"/> and related coordinate detail DTOs.
/// </summary>
public class RoutablePointTests
{
    [Fact]
    public void RoutablePoint_StoresCoordinates()
    {
        var point = new RoutablePoint
        {
            Name = "Main",
            Latitude = 10.5,
            Longitude = 20.25
        };

        Assert.Equal("Main", point.Name);
        Assert.Equal(10.5, point.Latitude);
        Assert.Equal(20.25, point.Longitude);
    }

    [Fact]
    public void RoutablePoint_DeserializesFromJson()
    {
        const string json = """
        {
          "name": "Corner",
          "latitude": 1.23,
          "longitude": 4.56
        }
        """;

        var point = JsonSerializer.Deserialize<RoutablePoint>(json);

        Assert.NotNull(point);
        Assert.Equal("Corner", point!.Name);
        Assert.Equal(1.23, point.Latitude);
        Assert.Equal(4.56, point.Longitude);
    }

    [Fact]
    public void CoordinatesDetail_DeserializesRoutablePoints()
    {
        const string json = """
        {
          "longitude": 12.34,
          "latitude": 56.78,
          "accuracy": "roof",
          "routable_points": [
            { "name": "Front", "latitude": 56.7801, "longitude": 12.3401 }
          ]
        }
        """;

        var detail = JsonSerializer.Deserialize<CoordinatesDetail>(json);

        Assert.NotNull(detail);
        Assert.Equal(12.34, detail!.Longitude);
        Assert.Equal(56.78, detail.Latitude);
        Assert.Equal("roof", detail.Accuracy);
        var rp = Assert.Single(detail.RoutablePoints);
        Assert.Equal("Front", rp.Name);
    }
}
