using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NetTopologySuite.Geometries;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Parsers;

public class GoogleTimelineJsonParser : ILocationDataParser
{
    private readonly ILogger<GoogleTimelineJsonParser> _logger;

    public GoogleTimelineJsonParser(ILogger<GoogleTimelineJsonParser> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<Location> ParseAsync(
        Stream fileStream,
        string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Parsing Google Timeline data for user {UserId}.", userId);
        using var text = new StreamReader(fileStream, leaveOpen: true);
        using var reader = new JsonTextReader(text) { CloseInput = false, MaxDepth = null };
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.TokenType != JsonToken.PropertyName ||
                !string.Equals((string?)reader.Value, "semanticSegments", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!await reader.ReadAsync(cancellationToken) || reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("semanticSegments must be an array.");
            while (await reader.ReadAsync(cancellationToken) && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType != JsonToken.StartObject)
                    throw new JsonReaderException("semanticSegments entries must be objects.");
                await foreach (var location in ReadSegmentAsync(reader, userId, cancellationToken))
                    yield return location;
            }
            yield break;
        }
        _logger.LogWarning("No semanticSegments found in JSON.");
    }

    private async IAsyncEnumerable<Location> ReadSegmentAsync(
        JsonTextReader reader,
        string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Position? position = null;
        while (await reader.ReadAsync(cancellationToken) && reader.TokenType != JsonToken.EndObject)
        {
            if (reader.TokenType != JsonToken.PropertyName) continue;
            var name = (string?)reader.Value;
            if (!await reader.ReadAsync(cancellationToken)) yield break;
            if (string.Equals(name, "timelinePath", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType != JsonToken.StartArray) continue;
                while (await reader.ReadAsync(cancellationToken) && reader.TokenType != JsonToken.EndArray)
                {
                    var item = await JObject.LoadAsync(reader, cancellationToken);
                    var timelinePoint = System.Text.Json.JsonSerializer.Deserialize<TimelinePath>(item.ToString());
                    if (TryParsePoint(timelinePoint?.Point ?? string.Empty, out var point) &&
                        DateTimeOffset.TryParse(timelinePoint?.Time, out var timestamp))
                        yield return MakeLocation(userId, point, timestamp, null, null, null, null);
                }
            }
            else if (string.Equals(name, "position", StringComparison.OrdinalIgnoreCase) &&
                     reader.TokenType == JsonToken.StartObject)
            {
                var item = await JObject.LoadAsync(reader, cancellationToken);
                position = System.Text.Json.JsonSerializer.Deserialize<Position>(item.ToString());
            }
            else await JsonReaderSkip.SkipValueAsync(reader, cancellationToken);
        }
        if (position?.LatLng != null && TryParsePoint(position.LatLng, out var result) &&
            DateTimeOffset.TryParse(position.Timestamp, out var when))
            yield return MakeLocation(userId, result, when, position.AccuracyMeters,
                position.AltitudeMeters, position.SpeedMetersPerSecond, position.Source);
    }

    // Helper: build your Domain Location entity
    private Location MakeLocation(
        string userId,
        Point pt,
        DateTimeOffset timestamp,
        double? accuracy,
        double? altitude,
        double? speed,
        string? notes)
    {
        return new Location
        {
            UserId = userId,
            Timestamp = timestamp.UtcDateTime,
            LocalTimestamp = timestamp.UtcDateTime,
            TimeZoneId = timestamp.Offset == TimeSpan.Zero
                             ? "UTC"
                             : timestamp.Offset.ToString(),
            Coordinates = pt,
            Accuracy = accuracy,
            Altitude = altitude,
            Speed = speed,
            Notes = notes
        };
    }

    // Helper: parse "40.8497007°, 25.869276°"
    //  └── parts[0] = latitude, parts[1] = longitude
    private bool TryParsePoint(string raw, out Point pt)
    {
        pt = null!;
        var parts = raw.Replace("°", "")
                       .Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        // CORRECTED: first element is LATITUDE
        if (!double.TryParse(parts[0], out double lat)) return false;
        // second element is LONGITUDE
        if (!double.TryParse(parts[1], out double lng)) return false;

        // Point expects (longitude, latitude)
        pt = new Point(lng, lat) { SRID = 4326 };
        return true;
    }

    private class Position
    {
        [JsonPropertyName("LatLng")]
        public string? LatLng { get; set; }

        [JsonPropertyName("accuracyMeters")]
        public double? AccuracyMeters { get; set; }

        [JsonPropertyName("altitudeMeters")]
        public double? AltitudeMeters { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("speedMetersPerSecond")]
        public double? SpeedMetersPerSecond { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }

    private sealed class TimelinePath
    {
        [JsonPropertyName("point")]
        public string? Point { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }
}

/// <summary>Skips one JSON value without retaining its object graph.</summary>
internal static class JsonReaderSkip
{
    internal static async Task SkipValueAsync(JsonReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.TokenType is not (JsonToken.StartArray or JsonToken.StartObject)) return;
        var depth = 1;
        while (depth > 0)
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new JsonReaderException("Unexpected end of JSON while skipping a value.");
            if (reader.TokenType is JsonToken.StartArray or JsonToken.StartObject) depth++;
            else if (reader.TokenType is JsonToken.EndArray or JsonToken.EndObject) depth--;
        }
    }
}
