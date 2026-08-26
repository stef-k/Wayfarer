using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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
