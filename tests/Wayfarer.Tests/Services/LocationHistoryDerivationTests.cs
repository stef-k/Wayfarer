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
    [InlineData("façade ação școală Łódź ắộơ", "FACADE ACAO SCOALA ŁODZ AOO")]
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

    [Fact]
    public void DiscardedLegacyBlocksAndSingleEntityDecodingPreserveVisibleWords()
    {
        Assert.Equal("TOKYO STATION CAFE &AMP; TEA", LocationHistoryDerivation.NormalizeText(
            RichNotes.ToPlainText("<div>Tokyo</div><div>station</div><table><tr><td>café</td><td>&amp;amp; tea</td></tr></table>")));
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

    /// <summary>R1 changed historical rules and parser behavior; ordinary recent controls cannot waive either rebuild.</summary>
    [Fact]
    public void R1CounterexamplesUseTheQualifiedSemanticGenerations()
    {
        var row = Row();
        row.Coordinates = new Point(-6.2603, 53.3498) { SRID = 4326 };
        row.LocalTimestamp = new DateTime(1925, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        row.Notes = "<math><annotation-xml encoding='TEXT/HTML'><style><p>secret</p></style></annotation-xml></math><p>visible</p>";
        LocationHistoryDerivation.Apply(row, true, true);
        Assert.Equal(row.LocalTimestamp.Ticks, row.CaptureLocalTimestamp!.Value.Ticks);
        Assert.Equal(0, row.CaptureOffsetSeconds); // Old-main TZDB returned +3600.
        Assert.Equal("VISIBLE", row.HistorySearchText); // Old-main parser exposed SECRET VISIBLE.
        Assert.Equal("history-time-1;GeoTimeZone-6.1.0;TZDB: 2026e (mapping: 49)", row.HistoryTimeVersion);
        Assert.Equal("history-text-1;RichNotes-2;AngleSharp-1.7.2.0;runtime-10.0.14;unicode-31129-0000799900000000000000000000007f", row.HistoryTextVersion);
    }

    /// <summary>Unknown time and text profiles reject before touching either selected generation, including invalid evidence.</summary>
    [Fact]
    public void QualificationIsIndependentAndNeverBecomesUnresolvedEvidence()
    {
        var current = HistoryDerivationEnvironment.Current;
        Assert.True(current.TimeQualified); Assert.True(current.TextQualified);
        var row = Row(); row.Coordinates = new Point(double.NaN, 10) { SRID = 4326 };
        var unknownText = current with { Parser = "unreviewed-parser" };
        Assert.True(unknownText.TimeQualified); Assert.False(unknownText.TextQualified);
        Assert.Throws<HistoryQualificationException>(() => LocationHistoryDerivation.Apply(row, true, true, unknownText));
        Assert.Null(row.HistoryTimeStatus); Assert.Null(row.HistoryTimeVersion); Assert.Null(row.HistoryTextVersion);
        var unknownTime = current with { Tzdb = "unreviewed-rules" };
        Assert.False(unknownTime.TimeQualified); Assert.True(unknownTime.TextQualified);
        Assert.Throws<HistoryQualificationException>(() => LocationHistoryDerivation.Apply(row, true, false, unknownTime));
        Assert.Null(row.HistoryTimeStatus);
        foreach (var unknown in new[] { current with { Runtime = new Version(10, 0, 15) },
                     current with { SortVersion = 0 }, current with { IcuNormalization = false } })
            Assert.Throws<HistoryQualificationException>(() => LocationHistoryDerivation.NormalizeText("café", unknown));
        LocationHistoryDerivation.Apply(row, false, true, unknownTime);
        Assert.Equal(LocationHistoryDerivation.TextVersion, row.HistoryTextVersion);
    }

    /// <summary>Canonical fixtures intentionally retain a conflicting imported zone.</summary>
    private static Location Row() => new()
    {
        UserId = "synthetic", TimeZoneId = "UTC", Coordinates = new Point(-74.006, 40.713) { SRID = 4326 },
        LocalTimestamp = new DateTime(2025, 10, 10, 12, 0, 0, DateTimeKind.Utc), Timestamp = DateTime.UtcNow
    };
}
