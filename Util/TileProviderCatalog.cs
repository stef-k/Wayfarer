using System.Globalization;
using System.IO;
using System.Net;
using Wayfarer.Models;

namespace Wayfarer.Util;

/// <summary>
/// Defines supported tile providers and validates tile URL templates.
/// </summary>
public static class TileProviderCatalog
{
    /// <summary>
    /// Special key used for custom tile providers.
    /// </summary>
    public const string CustomProviderKey = "custom";

    /// <summary>
    /// Preset tile providers that admins can choose from.
    /// </summary>
    public static readonly IReadOnlyList<TileProviderDefinition> Presets = new[]
    {
        new TileProviderDefinition(
            ApplicationSettings.DefaultTileProviderKey,
            "OpenStreetMap (Standard)",
            ApplicationSettings.DefaultTileProviderUrlTemplate,
            ApplicationSettings.DefaultTileProviderAttribution,
            requiresApiKey: false),
        new TileProviderDefinition(
            "carto-positron",
            "CARTO Positron (Light)",
            "https://cartodb-basemaps-a.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png",
            "&copy; OpenStreetMap contributors &copy; CARTO",
            requiresApiKey: false),
        new TileProviderDefinition(
            "carto-dark",
            "CARTO Dark Matter",
            "https://cartodb-basemaps-a.global.ssl.fastly.net/dark_all/{z}/{x}/{y}.png",
            "&copy; OpenStreetMap contributors &copy; CARTO",
            requiresApiKey: false),
        new TileProviderDefinition(
            "opentopomap",
            "OpenTopoMap",
            "https://a.tile.opentopomap.org/{z}/{x}/{y}.png",
            "&copy; OpenStreetMap contributors, SRTM | &copy; OpenTopoMap",
            requiresApiKey: false),
        new TileProviderDefinition(
            "thunderforest-cycle",
            "Thunderforest Cycle (API key required)",
            "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png?apikey={apiKey}",
            "&copy; OpenStreetMap contributors &copy; Thunderforest",
            requiresApiKey: true)
    };

    /// <summary>
    /// Attempts to resolve a preset provider by key.
    /// </summary>
    public static TileProviderDefinition? FindPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Presets.FirstOrDefault(p => p.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validates a tile URL template and ensures it points to a public HTTPS host.
    /// </summary>
    public static bool TryValidateTemplate(string template, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Tile URL template is required.";
            return false;
        }

        var trimmed = template.Trim();
        if (!ContainsRequiredPlaceholders(trimmed))
        {
            error = "Tile URL template must include {z}, {x}, and {y} placeholders.";
            return false;
        }

        if (!TryCreateTemplateUri(trimmed, out var templateUri, out error))
        {
            return false;
        }

        if (!templateUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Tile URL template must use HTTPS.";
            return false;
        }

        if (!string.Equals(Path.GetExtension(templateUri.AbsolutePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "Tile URL template must point to a .png resource.";
            return false;
        }

        if (!IsPublicHost(templateUri.Host))
        {
            error = "Tile URL template host must be public (localhost and private ranges are not allowed).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a concrete tile URL for the supplied coordinates.
    /// </summary>
    public static bool TryBuildTileUrl(string template, string? apiKey, int z, int x, int y, out string tileUrl, out string error)
    {
        tileUrl = string.Empty;
        if (!TryValidateTemplate(template, out error))
        {
            return false;
        }

        if (RequiresApiKey(template) && string.IsNullOrWhiteSpace(apiKey))
        {
            error = "Tile provider API key is required for the selected provider.";
            return false;
        }

        tileUrl = template
            .Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            tileUrl = tileUrl.Replace("{apiKey}", apiKey, StringComparison.OrdinalIgnoreCase);
        }

        if (!Uri.TryCreate(tileUrl, UriKind.Absolute, out _))
        {
            error = "Tile URL template produced an invalid URL.";
            tileUrl = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Indicates whether the template expects an API key placeholder.
    /// </summary>
    public static bool RequiresApiKey(string template)
    {
        return template.Contains("{apiKey}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRequiredPlaceholders(string template)
    {
        return template.Contains("{z}", StringComparison.OrdinalIgnoreCase)
               && template.Contains("{x}", StringComparison.OrdinalIgnoreCase)
               && template.Contains("{y}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateTemplateUri(string template, out Uri uri, out string error)
    {
        var sample = template
            .Replace("{z}", "0", StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", "0", StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", "0", StringComparison.OrdinalIgnoreCase)
            .Replace("{apiKey}", "placeholder", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(sample, UriKind.Absolute, out uri!))
        {
            error = "Tile URL template must be a valid absolute URL.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsPublicHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return true;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return false;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return false;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return false;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Represents a preset tile provider definition.
/// </summary>
public sealed class TileProviderDefinition
{
    /// <summary>
    /// Creates a new tile provider definition.
    /// </summary>
    public TileProviderDefinition(string key, string name, string urlTemplate, string attribution, bool requiresApiKey)
    {
        Key = key;
        Name = name;
        UrlTemplate = urlTemplate;
        Attribution = attribution;
        RequiresApiKey = requiresApiKey;
    }

    /// <summary>
    /// Provider key stored in settings.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Display name for admin UI.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Template URL used to fetch tiles.
    /// </summary>
    public string UrlTemplate { get; }

    /// <summary>
    /// Attribution HTML shown in Leaflet.
    /// </summary>
    public string Attribution { get; }

    /// <summary>
    /// Whether this provider requires an API key.
    /// </summary>
    public bool RequiresApiKey { get; }
}
