using System.Globalization;

namespace Wayfarer.Parsers;

/// <summary>Reads the three optional native history fields without granting server presence authority.</summary>
internal static class ObservationEvidence
{
    /// <summary>Retains bounded origin assertions; absent or malformed optional metadata remains unknown.</summary>
    internal static string? ReadOrigin(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 24 ? text : null;

    /// <summary>Accepts only explicit-offset instants, never host-local interpretation of an unspecified clock.</summary>
    internal static DateTimeOffset? ReadInstant(string? text) =>
        text is { Length: >= 20 } && (text.EndsWith('Z') || text[^6] is '+' or '-' && text[^3] == ':')
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToUniversalTime() : null;

    /// <summary>Preserves signed elapsed assertions; negative age remains historical-only at ingestion.</summary>
    internal static long? ReadAge(string? text) => long.TryParse(text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;
}
