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
    [InlineData("/* comment with \"quotes\" */ \"\\ud800\"", true)]
    [InlineData("\"\\ud800\", \"Activity\":\"Walking\"", false)]
    [InlineData("\"Walking\", \"Activity\":\"\\udc00\"", true)]
    public async Task NativeActivity_RetainsInvalidityBeforeDecoderReplacement(string activity, bool invalid)
    {
        var json = $$$"""
            {"type":"FeatureCollection","features":[{"type":"Feature",
            "geometry":{"type":"Point","coordinates":[22,40]},
            "properties":{"TimestampUtc":"2026-10-01T00:00:00Z","Activity":{{{activity}}} } } ] }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        var location = Assert.Single(await parser.ParseAsync(stream, "owner"));
        Assert.Equal(invalid, location.ImportedActivityInvalid);
    }
}
