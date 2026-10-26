using System.Globalization;
using System.Text;
using GeoTimeZone;
using NodaTime;
using Wayfarer.Models;

namespace Wayfarer.Util;

/// <summary>One deterministic derivation for stored fields and future query normalization; no network effects.</summary>
internal static class LocationHistoryDerivation
{
    /// <summary>Opaque semantic ID retained from qualified R1 data; provenance is reported separately.</summary>
    internal const string TimeVersion = "history-time-1;GeoTimeZone-6.1.0;TZDB: 2026e (mapping: 49)";
    /// <summary>Opaque semantic ID: compatible qualified servicing must not rename or restamp existing rows.</summary>
    internal const string TextVersion = "history-text-1;RichNotes-2;AngleSharp-1.7.2.0;runtime-10.0.14;unicode-31129-0000799900000000000000000000007f";

    /// <summary>Only these columns are excluded from canonical snapshot comparison and accepted owner edits.</summary>
    internal static bool IsDerived(string name) => name is nameof(Location.CaptureLocalTimestamp)
        or nameof(Location.CaptureTimeZoneId) or nameof(Location.CaptureOffsetSeconds)
        or nameof(Location.HistoryTimeVersion) or nameof(Location.HistoryTimeStatus)
        or nameof(Location.HistoryTimeFailureCode) or nameof(Location.HistorySearchText) or nameof(Location.HistoryTextVersion);

    /// <summary>Dependencies are explicit so a partial edit can preserve an independent prepared generation.</summary>
    internal static bool IsTimeInput(string name) => name is nameof(Location.LocalTimestamp) or nameof(Location.Coordinates);
    /// <summary>The eleven fields are the complete accepted history search surface.</summary>
    internal static bool IsTextInput(string name) => name is nameof(Location.Notes) or nameof(Location.Address)
        or nameof(Location.FullAddress) or nameof(Location.ProviderAddressLine1) or nameof(Location.AddressNumber)
        or nameof(Location.StreetName) or nameof(Location.PostCode) or nameof(Location.Place) or nameof(Location.Region)
        or nameof(Location.Country) or nameof(Location.ResolvedFeatureName);

    /// <summary>Updates selected derivations only, leaving canonical originals and the other generation intact.</summary>
    internal static void Apply(Location row, bool time, bool text, HistoryDerivationEnvironment? environment = null)
    {
        environment ??= HistoryDerivationEnvironment.Current;
        environment.Require(time, text);
        if (time) DeriveTime(row);
        if (!text) return;
        string?[] fields = [RichNotes.ToPlainText(row.Notes), row.Address, row.FullAddress, row.ProviderAddressLine1,
            row.AddressNumber, row.StreetName, row.PostCode, row.Place, row.Region, row.Country, row.ResolvedFeatureName];
        row.HistorySearchText = string.Join('\n', fields.Select(value => NormalizeText(value, environment)).Where(value => value.Length != 0));
        row.HistoryTextVersion = TextVersion;
    }

    /// <summary>NFKD, selected Latin/Greek accents, invariant uppercase, whitespace collapse and NFC; literal matching.</summary>
    internal static string NormalizeText(string? value, HistoryDerivationEnvironment? environment = null)
    {
        if (string.IsNullOrEmpty(value)) return "";
        (environment ?? HistoryDerivationEnvironment.Current).Require(false, true);
        var result = new StringBuilder(value.Length);
        var accentBase = false;
        var space = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = result.Length != 0; accentBase = false; continue; }
            var category = Rune.GetUnicodeCategory(rune);
            var mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (mark && accentBase && IsAccent(rune.Value)) continue;
            if (space) { result.Append(' '); space = false; }
            result.Append(Rune.ToUpperInvariant(rune).ToString());
            if (!mark) accentBase = Rune.IsLetter(rune) && (rune.Value is >= 0x41 and <= 0x24f or >= 0x370 and <= 0x3ff or >= 0x1e00 and <= 0x1eff);
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Accents only: preserves iota subscript, virama, Arabic vowels, Japanese dakuten and other marks.</summary>
    private static bool IsAccent(int scalar) => scalar is 0x300 or 0x301 or 0x302 or 0x303 or 0x304 or 0x306
        or 0x307 or 0x308 or 0x30a or 0x30b or 0x30c or 0x313 or 0x314 or 0x31b
        or 0x323 or 0x324 or 0x325 or 0x326 or 0x327 or 0x328 or 0x32d or 0x32e or 0x330 or 0x331 or 0x342;

    /// <summary>Validates evidence before the resolver; unexpected library/operational failures propagate without a stamp.</summary>
    private static void DeriveTime(Location row)
    {
        row.CaptureLocalTimestamp = null;
        row.CaptureTimeZoneId = null;
        row.CaptureOffsetSeconds = null;
        row.HistoryTimeVersion = TimeVersion;
        row.HistoryTimeStatus = "unresolved";
        row.HistoryTimeFailureCode = null;
        var point = row.Coordinates;
        if (point == null || point.IsEmpty || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X is < -180 or > 180 || point.Y is < -90 or > 90 || point.SRID != 4326)
        { row.HistoryTimeFailureCode = "invalidCoordinates"; return; }
        if (row.LocalTimestamp.Kind == DateTimeKind.Local || row.LocalTimestamp == DateTime.MinValue || row.LocalTimestamp == DateTime.MaxValue)
        { row.HistoryTimeFailureCode = "invalidEventUtc"; return; }
        var zoneId = TimeZoneLookup.GetTimeZone(point.Y, point.X).Result;
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId);
        if (zone == null) { row.HistoryTimeFailureCode = "zoneUnavailable"; return; }
        try
        {
            var value = Instant.FromDateTimeUtc(DateTime.SpecifyKind(row.LocalTimestamp, DateTimeKind.Utc)).InZone(zone);
            row.CaptureLocalTimestamp = value.LocalDateTime.ToDateTimeUnspecified();
            row.CaptureTimeZoneId = zoneId;
            row.CaptureOffsetSeconds = value.Offset.Seconds;
            row.HistoryTimeStatus = "resolved";
        }
        catch (ArgumentOutOfRangeException) { row.HistoryTimeFailureCode = "captureOutOfRange"; }
        catch (OverflowException) { row.HistoryTimeFailureCode = "captureOutOfRange"; }
    }
}
