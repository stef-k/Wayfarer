using System.Globalization;
using System.Text.RegularExpressions;

namespace Wayfarer.Parsers;

/// <summary>Finite shared clock and observation parsing, independent of host timezone and live authority.</summary>
internal static class ObservationEvidence
{
    private static readonly Regex InstantGrammar = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z");
    private static readonly Regex LocalGrammar = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?\z");

    /// <summary>Retains bounded origin assertions; absent or malformed legacy metadata remains unknown.</summary>
    internal static string? ReadOrigin(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 24 ? text : null;

    /// <summary>Discards fraction digits after six before binding; offset conversion alone may cross a second.</summary>
    internal static DateTimeOffset? ReadInstant(string? text)
    {
        if (text == null || !InstantGrammar.IsMatch(text)) return null;
        var dot = text.IndexOf('.');
        var end = text.EndsWith('Z') ? text.Length - 1 : text.Length - 6;
        var bounded = dot < 0 ? text : text[..(dot + 1)] + text[(dot + 1)..end].PadRight(6, '0')[..6] + text[end..];
        if (!DateTimeOffset.TryParseExact(bounded, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.ffffffK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return null;
        var utc = value.ToUniversalTime();
        return utc == DateTimeOffset.MinValue || utc == DateTimeOffset.MaxValue ? null : utc;
    }

    /// <summary>Canonical shared instants always emit six fraction digits and Z.</summary>
    internal static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Truncates generated instants at the same persistence boundary.</summary>
    internal static DateTime Truncate(DateTime value) => new(value.Ticks / 10 * 10, value.Kind);

    /// <summary>Validates exact local calendar text without converting or rounding its original fraction.</summary>
    internal static bool ValidOriginalLocal(string? text) => text == null || LocalGrammar.IsMatch(text)
        && DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Original identifiers are bounded platform evidence, never a lookup or execution authority.</summary>
    internal static bool ValidOriginalZone(string? text) => text == null || Regex.IsMatch(text, @"\A[A-Za-z0-9_+./:~-]{1,128}\z");

    /// <summary>Preserves signed elapsed assertions; negative age remains historical-only at ingestion.</summary>
    internal static long? ReadAge(string? text) => long.TryParse(text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;
}
