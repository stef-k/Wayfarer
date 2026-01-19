using Ganss.Xss;

namespace Wayfarer.Util;

/// <summary>
/// Provides HTML sanitization utilities to prevent XSS attacks in user-provided content.
/// </summary>
public static class HtmlSanitization
{
    /// <summary>
    /// Lazy-initialized sanitizer configured for map attribution strings.
    /// Allows only anchor tags with safe attributes for linking to data sources.
    /// </summary>
    private static readonly Lazy<HtmlSanitizer> AttributionSanitizer = new(() =>
    {
        var sanitizer = new HtmlSanitizer();

        // Clear all defaults and allow only what's needed for attributions
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.Add("a");
        sanitizer.AllowedTags.Add("span");
        sanitizer.AllowedTags.Add("strong");
        sanitizer.AllowedTags.Add("em");

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("target");
        sanitizer.AllowedAttributes.Add("title");
        sanitizer.AllowedAttributes.Add("class");

        // Only allow http/https links and fragment identifiers
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");

        // Disallow all CSS (prevents CSS-based attacks)
        sanitizer.AllowedCssProperties.Clear();

        return sanitizer;
    });

    /// <summary>
    /// Sanitizes HTML content intended for map attribution display.
    /// Allows only safe anchor tags for linking to data source providers.
    /// </summary>
    /// <param name="html">The raw HTML string to sanitize.</param>
    /// <returns>Sanitized HTML safe for rendering in attribution controls.</returns>
    /// <example>
    /// Input:  &lt;a href="https://osm.org" onclick="evil()"&gt;OSM&lt;/a&gt;&lt;script&gt;alert(1)&lt;/script&gt;
    /// Output: &lt;a href="https://osm.org"&gt;OSM&lt;/a&gt;
    /// </example>
    public static string SanitizeAttribution(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return AttributionSanitizer.Value.Sanitize(html);
    }
}
