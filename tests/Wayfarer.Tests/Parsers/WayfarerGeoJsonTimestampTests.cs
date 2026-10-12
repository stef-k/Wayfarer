using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Protects explicit UTC instants and the legacy no-offset GeoJSON interpretation.</summary>
public sealed class WayfarerGeoJsonTimestampTests
{
    /// <summary>Explicit designators convert to UTC; unzoned historical values retain their components.</summary>
    [Theory]
    [InlineData("2026-08-22T10:00:00+02:00", 8, 0)]
    [InlineData("2026-08-22T04:30:00-03:30", 8, 0)]
    [InlineData("2026-08-22T06:15:00Z", 6, 15)]
    [InlineData("2026-08-22T10:00:00", 10, 0)]
    [InlineData("2026-08-22T10:00:00+0200", 8, 0)]
    [InlineData(" 08/22/2026 10:00:00 +02:00 ", 8, 0)]
    [InlineData("08/22/2026 10:00:00", 10, 0)]
    [InlineData("2026-08-22", 0, 0)]
    public async Task ParseAsync_LocalTimestamp_PreservesApprovedInterpretation(string raw, int hour, int minute)
    {
        var location = await ParseAsync(raw);

        Assert.Equal(new DateTime(2026, 8, 22, hour, minute, 0, DateTimeKind.Utc), location.LocalTimestamp);
        Assert.Equal(DateTimeKind.Utc, location.LocalTimestamp.Kind);
        Assert.Equal(new DateTime(2026, 8, 22, 11, 0, 0, DateTimeKind.Utc), location.Timestamp);
        Assert.Equal(DateTimeKind.Utc, location.Timestamp.Kind);
        Assert.Equal("Europe/Athens", location.TimeZoneId);
    }

    /// <summary>Missing, null, blank and invalid optional values keep the receipt timestamp fallback.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("invalid", false)]
    public async Task ParseAsync_UnusableLocalTimestamp_FallsBackToTimestampUtc(string? raw, bool omit)
    {
        var location = await ParseAsync(raw, omit);

        Assert.Equal(new DateTime(2026, 8, 22, 11, 0, 0, DateTimeKind.Utc), location.LocalTimestamp);
        Assert.Equal(location.Timestamp, location.LocalTimestamp);
        Assert.Equal(DateTimeKind.Utc, location.LocalTimestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, location.Timestamp.Kind);
    }

    /// <summary>Parses one native feature, keeping its receipt timestamp distinct from valid event times.</summary>
    private static async Task<Wayfarer.Models.Location> ParseAsync(string? raw, bool omit = false)
    {
        var properties = new Dictionary<string, object?>
        {
            ["TimestampUtc"] = "2026-08-22T11:00:00Z",
            ["TimeZoneId"] = "Europe/Athens"
        };
        if (!omit) properties["LocalTimestamp"] = raw;
        var json = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = new[]
            {
                new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { 22.2, 40.1 } }, properties }
            }
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        return Assert.Single(await parser.ParseAsync(stream, "timestamp-owner"));
    }
}
