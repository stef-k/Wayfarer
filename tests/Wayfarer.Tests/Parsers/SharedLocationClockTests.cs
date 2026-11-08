using Wayfarer.Parsers;
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
