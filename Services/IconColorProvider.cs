using System.Text.RegularExpressions;

namespace Wayfarer.Services;

/// <summary>
/// Provides the map icon color classes exposed by the icon CSS bundle.
/// </summary>
public interface IIconColorProvider
{
    /// <summary>
    /// Reads the available background and glyph color classes, or returns <c>null</c> when the CSS bundle is missing.
    /// </summary>
    IconColorClasses? GetAvailableColors();
}

/// <summary>
/// Background and glyph color classes available to map icons.
/// </summary>
public sealed record IconColorClasses(IReadOnlyList<string> Backgrounds, IReadOnlyList<string> Glyphs);

/// <summary>
/// Reads icon color classes from the generated Wayfarer map icon CSS file.
/// </summary>
public sealed class IconColorProvider : IIconColorProvider
{
    private readonly IWebHostEnvironment _environment;

    /// <summary>
    /// Initializes a provider backed by the current web root.
    /// </summary>
    public IconColorProvider(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    public IconColorClasses? GetAvailableColors()
    {
        var cssPath = Path.Combine(_environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css");
        if (!File.Exists(cssPath))
        {
            return null;
        }

        var cssContent = File.ReadAllText(cssPath);
        return new IconColorClasses(
            ReadClasses(cssContent, @"\.bg-[\w-]+"),
            ReadClasses(cssContent, @"\.color-[\w-]+"));
    }

    private static IReadOnlyList<string> ReadClasses(string cssContent, string pattern) =>
        Regex.Matches(cssContent, pattern)
            .Select(match => match.Value.TrimStart('.'))
            .Distinct()
            .OrderBy(colorClass => colorClass)
            .ToList();
}
