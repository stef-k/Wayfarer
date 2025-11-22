using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Xunit;

namespace Wayfarer.Tests.Converters;

/// <summary>
/// Covers serialization/deserialization behavior for PointJsonConverter.
/// </summary>
public class PointJsonConverterTests
{
    [Fact]
    public void Write_WritesLongitudeLatitudeObject()
    {
        var point = new Point(23.5, 38.9) { SRID = 4326 };
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PointJsonConverter());

        var json = JsonSerializer.Serialize(point, options);

        Assert.Equal("{\"longitude\":23.5,\"latitude\":38.9}", json);
    }

    [Fact]
    public void Write_NullPoint_WritesNull()
    {
        Point? point = null;
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PointJsonConverter());

        var json = JsonSerializer.Serialize(point, options);

        Assert.Equal("null", json);
    }

    [Fact]
    public void Read_Throws_NotImplemented()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PointJsonConverter());
        var json = "{\"longitude\":1,\"latitude\":2}";
        Assert.Throws<NotImplementedException>(() =>
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            new PointJsonConverter().Read(ref reader, typeof(Point), options);
        });
    }
}
