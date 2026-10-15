using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Wayfarer.Models.Enums;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Qualifies native Activity invalid-value intake and independent optional LocationType text.</summary>
public sealed class ActivityImportValueTests
{
    /// <summary>Wrong types and lone escapes are invalid; optional and valid Unicode/literal escapes remain unchanged.</summary>
    [Theory]
    [InlineData("42", true)]
    [InlineData("{}", true)]
    [InlineData("[]", true)]
    [InlineData("null", false)]
    [InlineData("\" \\t\"", false)]
    [InlineData("\"\\ud800\"", true)]
    [InlineData("\"\\udc00\"", true)]
    [InlineData("\"\\ud800x\"", true)]
    [InlineData("\"\\ud83d\\ude80\"", false)]
    [InlineData("\"\\\\ud800\"", false)]
    [InlineData("\"\ufffd\"", false)]
    [InlineData("\"Περπάτημα 🚀\"", false)]
    [InlineData("\"\\ufffd\"", false)]
    [InlineData("/* comment with \"quotes\" */ \"\\ud800\"", true)]
    [InlineData("\"\\ud800\", \"Activity\":\"Walking\"", false)]
    [InlineData("\"Walking\", \"Activity\":\"\\udc00\"", true)]
    public async Task NativeActivity_RetainsInvalidityBeforeDecoderReplacement(string activity, bool invalid)
    {
        var json = $$$"""
            {"type":"FeatureCollection","features":[{"type":"Feature",
            "geometry":{"type":"Point","coordinates":[22,40]},
            "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":{{{activity}}},"LocationType":" Manual "} } ] }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        var location = Assert.Single(await parser.ParseAsync(stream, "owner"));
        Assert.Equal(invalid, location.ImportedActivityInvalid);
        Assert.Equal(" Manual ", location.LocationType);
    }

    /// <summary>LocationType accepts only nonempty strings without changing malformed Activity classification.</summary>
    [Theory]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("42", null)]
    [InlineData("true", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("\" \\t\\r\\n \"", " \t\r\n ")]
    [InlineData("\" MiXeD \\u00a0<& \"", " MiXeD \u00a0<& ")]
    public async Task NativeLocationType_IgnoresUnexpectedTypesAndPreservesStrings(string value, string? expected)
    {
        var json = $$$"""
            {"type":"FeatureCollection","features":[{"type":"Feature",
            "geometry":{"type":"Point","coordinates":[22,40]},
            "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":42,"LocationType":{{{value}}}}}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);

        var location = Assert.Single(await parser.ParseAsync(stream, "destination"));

        Assert.Equal(expected, location.LocationType);
        Assert.True(location.ImportedActivityInvalid);
        Assert.Null(location.ImportedActivityName);
        Assert.Equal("destination", location.UserId);
    }

    /// <summary>Explicit empty XML values and both CSV empty-cell encodings retain optional null semantics.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv, "Latitude,Longitude,TimestampUtc,LocationType\r\n40,22,2026-10-01T00:00:00Z,\r\n")]
    [InlineData(LocationImportFileType.Csv, "Latitude,Longitude,TimestampUtc,LocationType\r\n40,22,2026-10-01T00:00:00Z,\"\"\r\n")]
    [InlineData(LocationImportFileType.Gpx, """
        <gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:wf="https://wayfarer.app/schemas/gpx"><trk><trkseg>
        <trkpt lat="40" lon="22"><time>2026-10-01T00:00:00Z</time><extensions><wf:locationType/></extensions></trkpt>
        </trkseg></trk></gpx>
        """)]
    [InlineData(LocationImportFileType.Kml, """
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><name>2026-10-01T00:00:00Z</name>
        <ExtendedData><Data name="LocationType"><value/></Data></ExtendedData>
        <Point><coordinates>22,40</coordinates></Point></Placemark></Document></kml>
        """)]
    public async Task NativeLocationType_EmptyWireValuesBecomeNull(LocationImportFileType format, string payload)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);

        var location = Assert.Single(await parser.ParseAsync(stream, "owner"));

        Assert.Null(location.LocationType);
        Assert.Null(location.ImportedActivityName);
    }

    /// <summary>Fallback text must be nonempty canonical Base64 and decode with strict UTF-8.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("Cw")]
    [InlineData("Cw===")]
    [InlineData("Cw== ")]
    [InlineData("Cx==")]
    [InlineData("/w==")]
    [InlineData("wIA=")]
    [InlineData("7aCA")]
    public async Task XmlLocationType_RejectsMalformedFallback(string encoded)
    {
        await AssertInvalidXmlLocationTypeAsync(
            $"<extensions><wf:locationTypeUtf8Base64V1>{encoded}</wf:locationTypeUtf8Base64V1></extensions>",
            $"<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>{encoded}</value></Data></ExtendedData>");
    }

    /// <summary>Presence, including empty ordinary fields, never gives one representation precedence over another.</summary>
    [Theory]
    [InlineData(
        "<extensions><wf:locationType/><wf:locationTypeUtf8Base64V1>Cw==</wf:locationTypeUtf8Base64V1></extensions>",
        "<ExtendedData><Data name=\"LocationType\"/><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>Cw==</value></Data></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1>Cw==</wf:locationTypeUtf8Base64V1><wf:LOCATIONTYPEUTF8BASE64V1>Cw==</wf:LOCATIONTYPEUTF8BASE64V1></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>Cw==</value></Data><Data name=\"WAYFARERLOCATIONTYPEUTF8BASE64V1\"><value>Cw==</value></Data></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1>Cw==</wf:locationTypeUtf8Base64V1></extensions><extensions><wf:locationType>Manual</wf:locationType></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>Cw==</value></Data></ExtendedData><ExtendedData><Data name=\"LocationType\"><value>Manual</value></Data></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1>Cw==</wf:locationTypeUtf8Base64V1></extensions><extensions><wf:locationTypeUtf8Base64V1>SG9tZQE=</wf:locationTypeUtf8Base64V1></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>Cw==</value></Data></ExtendedData><ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>SG9tZQE=</value></Data></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1><wf:part>Cw==</wf:part></wf:locationTypeUtf8Base64V1></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value><part>Cw==</part></value></Data></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1/></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"/></ExtendedData>")]
    [InlineData(
        "<extensions><wf:locationTypeUtf8Base64V1>Cw==</wf:locationTypeUtf8Base64V1><wf:locationType>Manual</wf:locationType></extensions>",
        "<ExtendedData><Data name=\"WayfarerLocationTypeUtf8Base64V1\"><value>Cw==</value><value>SG9tZQE=</value></Data></ExtendedData>")]
    public Task XmlLocationType_RejectsAmbiguousFallback(string gpxFields, string kmlFields) =>
        AssertInvalidXmlLocationTypeAsync(gpxFields, kmlFields);

    /// <summary>Exercises both production subtree readers with the supplied malformed field shapes.</summary>
    private static async Task AssertInvalidXmlLocationTypeAsync(string gpxFields, string kmlFields)
    {
        foreach (var format in new[] { LocationImportFileType.Gpx, LocationImportFileType.Kml })
        {
            var payload = format == LocationImportFileType.Gpx ? $"""
                <gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:wf="https://wayfarer.app/schemas/gpx"><trk><trkseg>
                <trkpt lat="40" lon="22"><time>2026-10-01T00:00:00Z</time>{gpxFields}</trkpt>
                </trkseg></trk></gpx>
                """ : $"""
                <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><name>2026-10-01T00:00:00Z</name>
                {kmlFields}<Point><coordinates>22,40</coordinates></Point></Placemark></Document></kml>
                """;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
            var error = await Assert.ThrowsAsync<FormatException>(async () => await parser.ParseAsync(stream, "owner"));
            Assert.Contains("LocationType", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Only the retained properties.Activity determines invalidity, including replacement containers.</summary>
    [Theory]
    [InlineData("""
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"\ud800"},
        "extra":{"Activity":"ignored"}
        """, true, null)]
    [InlineData("""
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"Walking"},
        "extra":{"Activity":"\ud800"}
        """, false, "Walking")]
    [InlineData("""
        "properties":{"Activity":"\ud800"},
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z"}
        """, false, null)]
    [InlineData("""
        "properties":{"Activity":"\ud800"},
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"Walking"}
        """, false, "Walking")]
    [InlineData("""
        "properties":{"Activity":"Walking"},
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"\udc00"}
        """, true, null)]
    [InlineData("""
        "extra":{"Activity":"\ud800"},
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z"}
        """, false, null)]
    [InlineData("""
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z"},
        "extra":{"Activity":"\udc00"}
        """, false, null)]
    [InlineData("""
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"\ud800","Activity":"Walking"},
        "extra":{"Activity":"\udc00"}
        """, false, "Walking")]
    [InlineData("""
        "propert\u0069es":{"TimestampUtc":"2026-10-01T00:00:00Z","\u0041ctivity":"Walking"},
        "extra":{"Activity":"\ud800"}
        """, false, "Walking")]
    [InlineData("""
        "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":"Walking"},
        "extra.properties":{"Activity":"\ud800"}
        """, false, "Walking")]
    public async Task NativeActivity_UsesEffectivePropertiesValue(string members, bool invalid, string? activity)
    {
        var json = $$$"""
            {"type":"FeatureCollection","features":[{"type":"Feature",
            "geometry":{"type":"Point","coordinates":[22,40]},{{{members}}}}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);

        var location = Assert.Single(await parser.ParseAsync(stream, "owner"));

        Assert.Equal(invalid, location.ImportedActivityInvalid);
        if (!invalid) Assert.Equal(activity, location.ImportedActivityName);
    }

    /// <summary>Json.NET owns quoted/unquoted names, Unicode decoding, comments and duplicate-property semantics.</summary>
    [Theory]
    [InlineData("Activity", "\"Walking\"", false, "Walking")]
    [InlineData("'Activity'", "'\\ud800'", true, null)]
    [InlineData("\"\\u0041ctivity\"", "\"\\ud83d\\ude80\"", false, "🚀")]
    [InlineData("Activity", "\"\\ufffd\"", false, "\ufffd")]
    [InlineData("Activity", "/* \"quotes\" */ \"\\\\ud800\"", false, "\\ud800")]
    [InlineData("Activity", "\"\\ud800\",Activity: // \"quotes\"\n\"Walking\"", false, "Walking")]
    public async Task NativeActivity_PreservesJsonNetPropertySyntax(
        string property, string value, bool invalid, string? activity)
    {
        var json = $$$"""
            {type:"FeatureCollection",features:[{type:"Feature",
            geometry:{type:"Point",coordinates:[22,40]},
            properties:{TimestampUtc:"2026-10-01T00:00:00Z",{{{property}}}:{{{value}}}}}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);

        var location = Assert.Single(await parser.ParseAsync(stream, "owner"));

        Assert.Equal(invalid, location.ImportedActivityInvalid);
        if (!invalid) Assert.Equal(activity, location.ImportedActivityName);
    }
}
