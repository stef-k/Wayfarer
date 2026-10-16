using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Captures deterministic coordinate, timestamp and ignored-JSON import boundaries.</summary>
public sealed class BoundedImportFinalGapTests
{
    /// <summary>Every history point boundary retains valid extrema and metadata while skipping invalid ordinates.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv, false)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, false)]
    [InlineData(LocationImportFileType.Gpx, false)]
    [InlineData(LocationImportFileType.Kml, false)]
    [InlineData(LocationImportFileType.GoogleTimeline, false)]
    [InlineData(LocationImportFileType.GoogleTimeline, true)]
    public async Task Coordinates_MixedFilePreservesValidPoints(LocationImportFileType format, bool googlePosition)
    {
        var points = new (string Latitude, string Longitude)[]
        {
            ("90", "180"), ("90.000001", "22.2"), ("-90.000001", "22.2"),
            ("-90", "-180"), ("40.1", "180.000001"), ("40.1", "-180.000001"),
            ("0", "0")
        };
        // NTS rejects non-finite JSON numbers before a Point reaches the parser's ordinate guard.
        if (format != LocationImportFileType.WayfarerGeoJson)
            points = [.. points, ("NaN", "22.2"), ("40.1", "NaN"), ("Infinity", "22.2"), ("40.1", "-Infinity"),
                ("not-a-number", "22.2")];
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        var rows = await ParseAsync(parser, CoordinateFile(format, googlePosition, points));

        Assert.Equal(new[] { (180d, 90d), (-180d, -90d), (0d, 0d) },
            rows.Select(row => (row.Coordinates.X, row.Coordinates.Y)));
        Assert.All(rows, row =>
        {
            Assert.Equal("owner", row.UserId);
            Assert.Equal(4326, row.Coordinates.SRID);
            Assert.Equal(new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc), row.Timestamp);
            Assert.Equal(row.Timestamp, row.LocalTimestamp);
            Assert.Equal("UTC", row.TimeZoneId);
            var pathPoint = format == LocationImportFileType.GoogleTimeline && !googlePosition;
            Assert.Equal(pathPoint ? null : (double?)12.5, row.Altitude);
            Assert.Equal(pathPoint ? null : "Coordinate control", row.Notes);
            Assert.Equal(format is LocationImportFileType.Csv or LocationImportFileType.WayfarerGeoJson
                ? Guid.Parse("dca9eca2-233f-4f1f-bb36-49c8e6a65d61") : (Guid?)null, row.IdempotencyKey);
        });
    }

    /// <summary>Non-finite GeoJSON retains the reader's existing syntax failure rather than adding recovery parsing.</summary>
    [Fact]
    public async Task GeoJson_NonFiniteCoordinateRetainsSyntaxFailure()
    {
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        var input = CoordinateFile(LocationImportFileType.WayfarerGeoJson, false, [("NaN", "22.2")]);

        await Assert.ThrowsAsync<JsonReaderException>(() => ParseAsync(parser, input));
    }

    /// <summary>Uses ordinary native records and both supported Google point shapes without changing JSON recovery.</summary>
    private static string CoordinateFile(LocationImportFileType format, bool googlePosition,
        IEnumerable<(string Latitude, string Longitude)> points)
    {
        const string timestamp = "2026-08-25T00:00:00Z";
        const string key = "dca9eca2-233f-4f1f-bb36-49c8e6a65d61";
        return format switch
        {
            LocationImportFileType.Csv =>
                "Latitude,Longitude,TimestampUtc,IdempotencyKey,Altitude,Notes\r\n" +
                string.Join("\r\n", points.Select(p => $"{p.Latitude},{p.Longitude},{timestamp},{key},12.5,Coordinate control")),
            LocationImportFileType.WayfarerGeoJson =>
                """{"type":"FeatureCollection","features":[""" +
                string.Join(",", points.Select(p => $$$"""
                    {"type":"Feature","geometry":{"type":"Point","coordinates":[{{{p.Longitude}}},{{{p.Latitude}}}]},
                     "properties":{"TimestampUtc":"{{{timestamp}}}","IdempotencyKey":"{{{key}}}","Altitude":12.5,"Notes":"Coordinate control"}}
                    """)) + "]}",
            LocationImportFileType.Gpx =>
                """<gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:w="https://wayfarer.app/schemas/gpx"><trk><trkseg>""" +
                string.Concat(points.Select(p => $"""
                    <trkpt lat="{p.Latitude}" lon="{p.Longitude}"><time>{timestamp}</time><ele>12.5</ele>
                    <extensions><w:notes>Coordinate control</w:notes></extensions></trkpt>
                    """)) + "</trkseg></trk></gpx>",
            LocationImportFileType.Kml =>
                """<kml xmlns="http://www.opengis.net/kml/2.2"><Document>""" +
                string.Concat(points.Select(p => $"""
                    <Placemark><name>{timestamp}</name><Point><coordinates>{p.Longitude},{p.Latitude},12.5</coordinates></Point>
                    <description>Coordinate control</description></Placemark>
                    """)) + "</Document></kml>",
            LocationImportFileType.GoogleTimeline => """{"semanticSegments":[""" +
                string.Join(",", points.Select(p => googlePosition ? $$$"""
                    {"position":{"LatLng":"{{{p.Latitude}}}°, {{{p.Longitude}}}°","timestamp":"{{{timestamp}}}", "altitudeMeters":12.5,"source":"Coordinate control"}}
                    """ : $$$"""
                    {"timelinePath":[{"point":"{{{p.Latitude}}}°, {{{p.Longitude}}}°","time":"{{{timestamp}}}"}]}
                    """)) + "]}",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    [Fact]
    public async Task Csv_InvalidRequiredTimestamp_DoesNotChangeAcrossReplay()
    {
        const string csv = "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,not-a-time\r\n";
        var parser = new CsvLocationParser(NullLogger<CsvLocationParser>.Instance);

        var first = await ParseAsync(parser, csv);
        await Task.Delay(25);
        var replay = await ParseAsync(parser, csv);

        Assert.Empty(first);
        Assert.Empty(replay);
    }

    [Fact]
    public void IgnoredJsonCompositeValues_DoNotUseJTokenMaterialization()
    {
        var google = File.ReadAllText(SourcePath("Parsers", "GoogleTimelineJsonParser.cs"));
        var geoJson = File.ReadAllText(SourcePath("Parsers", "WayfarerGeoJsonParser.cs"));

        Assert.DoesNotContain("JToken.ReadFromAsync", google, StringComparison.Ordinal);
        Assert.DoesNotContain("JToken.ReadFromAsync", geoJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredTimestamp_InvalidRecordsAreSkippedByEveryAffectedFormat()
    {
        var cases = new (ILocationDataParser Parser, string Input)[]
        {
            (new CsvLocationParser(NullLogger<CsvLocationParser>.Instance),
                "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,bad\r\n"),
            (new GpxLocationParser(NullLogger<GpxLocationParser>.Instance),
                "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                "<trkpt lat=\"40.1\" lon=\"22.2\"><time>bad</time></trkpt></trkseg></trk></gpx>"),
            (new KmlLocationParser(NullLogger<KmlLocationParser>.Instance),
                "<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Placemark>" +
                "<name>bad</name><Point><coordinates>22.2,40.1</coordinates></Point></Placemark></kml>"),
            (new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance),
                "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\"," +
                "\"geometry\":{\"type\":\"Point\",\"coordinates\":[22.2,40.1]}," +
                "\"properties\":{\"TimestampUtc\":\"bad\"}}]}")
        };

        foreach (var (parser, input) in cases)
            Assert.Empty(await ParseAsync(parser, input));
    }

    [Fact]
    public async Task IgnoredJsonValue_NestedPayloadIsSkippedAndTruncationThrows()
    {
        var nested = string.Concat(Enumerable.Repeat("[{\"ignored\":", 250)) + "0" +
            string.Concat(Enumerable.Repeat("}]", 250));
        var input = "{\"unknown\":" + nested +
            ",\"type\":\"FeatureCollection\",\"features\":[]}";
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);

        Assert.Empty(await ParseAsync(parser, input));
        await Assert.ThrowsAsync<JsonReaderException>(() => ParseAsync(parser, "{\"unknown\":[{}"));
    }

    private static async Task<List<Wayfarer.Models.Location>> ParseAsync(
        ILocationDataParser parser, string value)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        var locations = new List<Wayfarer.Models.Location>();
        await foreach (var location in parser.ParseAsync(stream, "owner")) locations.Add(location);
        return locations;
    }

    private static string SourcePath(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
