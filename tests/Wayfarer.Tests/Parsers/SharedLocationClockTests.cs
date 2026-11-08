using Wayfarer.Parsers;
using System.Text.Json;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Shared clocks truncate raw fractions before framework date binding.</summary>
public sealed class SharedLocationClockTests
{
    /// <summary>Offset conversion and fractions cannot carry across a second, including before epochs.</summary>
    [Theory]
    [InlineData("2026-11-08T12:59:59.999999950+03:00", "2026-11-08T09:59:59.999999Z")]
    [InlineData("1999-12-31T23:59:59.999999900Z", "1999-12-31T23:59:59.999999Z")]
    [InlineData("1960-01-01T00:00:00.1Z", "1960-01-01T00:00:00.100000Z")]
    public void InstantTruncatesWithinSecond(string input, string expected) =>
        Assert.Equal(expected, ObservationEvidence.Format(ObservationEvidence.ReadInstant(input)!.Value));

    /// <summary>Provider infinity endpoints cannot acquire finite meaning merely by dropping fraction digits.</summary>
    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("9999-12-31T23:59:59.999999999Z")]
    public void InfinityEndpointIsUnavailable(string input) => Assert.Null(ObservationEvidence.ReadInstant(input));

    /// <summary>Explicit JSON instants never bind through host-local time; legacy wall-clock serialization stays offsetless.</summary>
    [Fact]
    public void CaptureBindingKeepsClockAuthorityExplicit()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bound = JsonSerializer.Deserialize<GpsLoggerLocationDto>("{\"timestamp\":\"9999-12-31T23:59:59.999999+00:00\"}", options)!;
        Assert.Equal(DateTimeKind.Utc, bound.Timestamp.Kind);
        Assert.Equal(9999990, bound.Timestamp.Ticks % TimeSpan.TicksPerSecond);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new GpsLoggerLocationDto { Timestamp = new DateTime(2026, 11, 8, 15, 0, 0) }, options));
        Assert.Equal("2026-11-08T15:00:00.000000", json.RootElement.GetProperty("timestamp").GetString());
    }

    /// <summary>Original zone evidence uses a bounded entire-token rule rather than a timezone database.</summary>
    [Theory]
    [InlineData("Europe/Athens", true)]
    [InlineData("GMT+03:00", true)]
    [InlineData("UTC-02:30", true)]
    [InlineData("+03:00", true)]
    [InlineData("GMT+0300", true)]
    [InlineData("UTC\n", false)]
    [InlineData("", false)]
    [InlineData("Αθήνα", false)]
    public void OriginalZoneIsDescriptive(string input, bool valid) =>
        Assert.Equal(valid, ObservationEvidence.ValidOriginalZone(input));
}
