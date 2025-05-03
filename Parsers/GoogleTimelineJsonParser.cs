using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task<List<Location>> ParseAsync(Stream fileStream, string userId)
    {
        _logger.LogInformation("Parsing Google Timeline data for user {UserId}.", userId);

        string json = await new StreamReader(fileStream).ReadToEndAsync();
        var opts = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };

        // 1) Deserialize the top-level wrapper
        var root = JsonSerializer.Deserialize<Root>(json, opts);
        if (root?.SemanticSegments == null || root.SemanticSegments.Count == 0)
        {
            _logger.LogWarning("No semanticSegments found in JSON.");
            return new List<Location>();
        }

        var locations = new List<Location>();

        foreach (var seg in root.SemanticSegments)
        {
            // 2) timelinePath (many points)
            if (seg.TimelinePath != null)
            {
                foreach (var tp in seg.TimelinePath)
                {
                    if (TryParsePoint(tp.Point, out var pt) &&
                        DateTimeOffset.TryParse(tp.Time, out var when))
                    {
                        locations.Add(MakeLocation(userId, pt, when, null, null, null, null));
                    }
                }
            }

            // 3) position (single point)
            if (seg.Position?.LatLng != null &&
                TryParsePoint(seg.Position.LatLng, out var p2) &&
                DateTimeOffset.TryParse(seg.Position.Timestamp, out var when2))
            {
                locations.Add(MakeLocation(
                    userId,
                    p2,
                    when2,
                    seg.Position.AccuracyMeters,
                    seg.Position.AltitudeMeters,
                    seg.Position.SpeedMetersPerSecond,
                    seg.Position.Source));
            }
        }

        _logger.LogInformation("Successfully parsed {Count} locations.", locations.Count);
        return locations;
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
        return new Location {
            UserId         = userId,
            Timestamp      = timestamp.UtcDateTime,
            LocalTimestamp = timestamp.UtcDateTime,
            TimeZoneId     = timestamp.Offset == TimeSpan.Zero
                             ? "UTC"
                             : timestamp.Offset.ToString(),
            Coordinates    = pt,
            Accuracy       = accuracy,
            Altitude       = altitude,
            Speed          = speed,
            Notes          = notes
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
        if (!double.TryParse(parts[0], out double lat))  return false;
        // second element is LONGITUDE
        if (!double.TryParse(parts[1], out double lng))  return false;

        // Point expects (longitude, latitude)
        pt = new Point(lng, lat) { SRID = 4326 };
        return true;
    }

    // Top-level wrapper
    private class Root
    {
        [JsonPropertyName("semanticSegments")]
        public List<Segment>? SemanticSegments { get; set; }
    }

    private class Segment
    {
        [JsonPropertyName("timelinePath")]
        public List<TimelinePath>? TimelinePath { get; set; }

        [JsonPropertyName("position")]
        public Position? Position { get; set; }
    }

    private class TimelinePath
    {
        [JsonPropertyName("point")]
        public string? Point { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
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
}
