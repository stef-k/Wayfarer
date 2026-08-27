using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Captures the final deterministic timestamp and ignored-JSON import gaps.</summary>
public sealed class BoundedImportFinalGapTests
{
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
