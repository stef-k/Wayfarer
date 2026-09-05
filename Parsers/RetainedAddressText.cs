namespace Wayfarer.Parsers;

/// <summary>Normalizes retained provider address text at Location import boundaries.</summary>
internal static class RetainedAddressText
{
    /// <summary>
    /// Trims outer whitespace and preserves internal text, including newlines and tabs.
    /// Missing, blank, or over-500-character imported values remain absent.
    /// </summary>
    internal static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 500 ? null : trimmed;
    }
}
