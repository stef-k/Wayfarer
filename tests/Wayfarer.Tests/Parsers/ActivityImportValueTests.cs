using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Qualifies the small invalid-value intake at the native parser seam.</summary>
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
