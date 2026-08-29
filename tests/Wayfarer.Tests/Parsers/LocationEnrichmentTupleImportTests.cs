using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Proves every Location-history format applies the shared enrichment tuple contract.</summary>
public sealed class LocationEnrichmentTupleImportTests
{
    /// <summary>Preserves valid metadata-free provenance across every Location-history format.</summary>
    [Theory]
    [InlineData("mapbox", "permanent")]
    [InlineData("geoapify", "persistent")]
    public async Task MetadataFreeProvenanceSurvivesEveryLocationHistoryFormat(string provider, string storageMode)
    {
        foreach (var (parser, input) in Cases(provider, storageMode))
        {
            var location = Assert.Single(await ParseAsync(parser, input));
            Assert.Equal((null, null, provider, storageMode),
                (location.ResolvedFeatureName, location.ResolvedFeatureType,
                    location.ReverseGeocodingProvider, location.ReverseGeocodingStorageMode));
            Assert.Equal(new DateTimeOffset(2026, 8, 27, 21, 30, 0, TimeSpan.Zero),
                location.ReverseGeocodedAt);
        }
    }

    private static IEnumerable<(ILocationDataParser Parser, string Input)> Cases(string provider, string storageMode)
    {
        yield return (new CsvLocationParser(NullLogger<CsvLocationParser>.Instance),
            "Latitude,Longitude,TimestampUtc,ReverseGeocodingProvider,ReverseGeocodingStorageMode,ReverseGeocodedAt\r\n" +
            $"40.1,22.2,2026-08-28T13:00:00Z,{provider},{storageMode},2026-08-28T00:30:00+03:00\r\n");
        yield return (new GpxLocationParser(NullLogger<GpxLocationParser>.Instance), $$"""
            <gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:wayfarer="https://wayfarer.app/schemas/gpx"><trk><trkseg>
            <trkpt lat="40.1" lon="22.2"><time>2026-08-28T13:00:00Z</time><extensions>
            <wayfarer:reverseGeocodingProvider>{{provider}}</wayfarer:reverseGeocodingProvider>
            <wayfarer:reverseGeocodingStorageMode>{{storageMode}}</wayfarer:reverseGeocodingStorageMode>
            <wayfarer:reverseGeocodedAt>2026-08-28T00:30:00+03:00</wayfarer:reverseGeocodedAt>
            </extensions></trkpt></trkseg></trk></gpx>
            """);
        yield return (new KmlLocationParser(NullLogger<KmlLocationParser>.Instance), $$"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><ExtendedData>
            <Data name="TimestampUtc"><value>2026-08-28T13:00:00Z</value></Data>
            <Data name="ReverseGeocodingProvider"><value>{{provider}}</value></Data>
            <Data name="ReverseGeocodingStorageMode"><value>{{storageMode}}</value></Data>
            <Data name="ReverseGeocodedAt"><value>2026-08-28T00:30:00+03:00</value></Data>
            </ExtendedData><Point><coordinates>22.2,40.1</coordinates></Point></Placemark></Document></kml>
            """);
        yield return (new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance), $$$"""
            {"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[22.2,40.1]},
            "properties":{"TimestampUtc":"2026-08-28T13:00:00Z","ReverseGeocodingProvider":"{{{provider}}}",
            "ReverseGeocodingStorageMode":"{{{storageMode}}}","ReverseGeocodedAt":"2026-08-28T00:30:00+03:00"}}]}
            """);
    }

    private static async Task<List<Location>> ParseAsync(ILocationDataParser parser, string value)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        var locations = new List<Location>();
        await foreach (var location in parser.ParseAsync(stream, "owner")) locations.Add(location);
        return locations;
    }
}
