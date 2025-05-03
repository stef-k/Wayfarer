using NetTopologySuite.Geometries;
using System.Text.Json;
using System.Text.Json.Serialization;

public class PointJsonConverter : JsonConverter<Point>
{
    public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Deserialize POINT data (if needed)
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
    {
        // Serialize the Point as a simple object containing coordinates
        if (value != null)
        {
            writer.WriteStartObject();
            writer.WriteNumber("longitude", value.X); // X = longitude
            writer.WriteNumber("latitude", value.Y);  // Y = latitude
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
