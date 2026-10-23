using System.Text.Json;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Owns deterministic capture evidence and visible literal text semantics.</summary>
public sealed class LocationHistoryDerivationTests
{
    [Theory]
    [InlineData("  CAFÉ\t au\n lait ", "CAFE AU LAIT")]
    [InlineData("cafe\u0301", "CAFE")]
    [InlineData("Καφές Αθήνα ΐ ΰ", "ΚΑΦΕΣ ΑΘΗΝΑ Ι Υ")]
    [InlineData("東京 ガイド العربية हिन्दी", "東京 ガイド العربية हिन्दी")]
    [InlineData("ＡＢＣ 100%_\\", "ABC 100%_\\")]
    public void NormalizationHasExplicitAccentAndScriptBoundaries(string value, string expected) =>
        Assert.Equal(expected, LocationHistoryDerivation.NormalizeText(value));

    [Fact]
    public void VisibleNotesPreserveInlineWordsBlocksAndFieldBoundaries()
    {
        var row = Row();
        row.Notes = "<p>To<strong>kyo</strong>&nbsp;station</p><ul><li>café</li><li>north</li></ul>"
            + "<a href='https://secret'>exit</a><script>removed</script><img src='https://image' alt='hidden'>";
        row.Address = "South road";
        LocationHistoryDerivation.Apply(row, true, true);
        Assert.Equal("TOKYO STATION CAFE NORTH EXIT\nSOUTH ROAD", row.HistorySearchText);
        Assert.DoesNotContain("EXIT SOUTH", row.HistorySearchText);
        row.Coordinates = null!; // Entity JSON shape is checked without the separate spatial converter.
        Assert.DoesNotContain("HistorySearchText", JsonSerializer.Serialize(row));
        Assert.Contains("<strong>", row.Notes);
    }

    [Theory]
    [InlineData("2025-11-02T05:30:00Z", "2025-11-02T01:30:00", -14400)]
    [InlineData("2025-11-02T06:30:00Z", "2025-11-02T01:30:00", -18000)]
    [InlineData("2025-03-09T07:30:00Z", "2025-03-09T03:30:00", -14400)]
    public void CaptureUsesEventInstantHistoricalRulesAndCoordinates(string utc, string local, int offset)
    {
        var row = Row();
        row.LocalTimestamp = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        LocationHistoryDerivation.Apply(row, true, false);
        Assert.Equal(DateTime.Parse(local), row.CaptureLocalTimestamp);
        Assert.Equal("America/New_York", row.CaptureTimeZoneId);
        Assert.Equal(offset, row.CaptureOffsetSeconds);
        Assert.Equal("UTC", row.TimeZoneId);
    }

    [Fact]
    public void NonHourOffsetsAndExceptionalEvidenceRemainDistinctFromPending()
    {
        var row = Row();
        Assert.Null(row.HistoryTimeVersion);
        row.Coordinates = new Point(85.324, 27.717) { SRID = 4326 };
        LocationHistoryDerivation.Apply(row, true, false);
        Assert.Equal(20700, row.CaptureOffsetSeconds);
        row.Coordinates = new Point(double.NaN, 10) { SRID = 4326 };
        LocationHistoryDerivation.Apply(row, true, false);
        Assert.Equal("unresolved", row.HistoryTimeStatus);
        Assert.Equal("invalidCoordinates", row.HistoryTimeFailureCode);
        Assert.Null(row.CaptureLocalTimestamp);
        Assert.Equal(LocationHistoryDerivation.TimeVersion, row.HistoryTimeVersion);
    }

    /// <summary>Canonical fixtures intentionally retain a conflicting imported zone.</summary>
    private static Location Row() => new()
    {
        UserId = "synthetic", TimeZoneId = "UTC", Coordinates = new Point(-74.006, 40.713) { SRID = 4326 },
        LocalTimestamp = new DateTime(2025, 10, 10, 12, 0, 0, DateTimeKind.Utc), Timestamp = DateTime.UtcNow
    };
}
