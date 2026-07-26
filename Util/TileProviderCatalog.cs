using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wayfarer.Models;
using Wayfarer.Services;

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
            "opentopomap",
            "OpenTopoMap",
            "https://a.tile.opentopomap.org/{z}/{x}/{y}.png",
            "Map data: &copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors, SRTM | Map style: &copy; <a href=\"https://opentopomap.org\">OpenTopoMap</a> (<a href=\"https://creativecommons.org/licenses/by-sa/3.0/\">CC-BY-SA</a>)",
            requiresApiKey: false)
    };

    private static readonly string[] ThunderforestKeys = ["thunderforest-cycle"];
    private static readonly string[] CartoKeys = ["carto-positron", "carto-dark"];
    private static readonly string[] ThunderforestHosts = ["tile.thunderforest.com", "api.thunderforest.com"];
    private static readonly string[] CartoHosts =
    [
        "cartodb-basemaps-a.global.ssl.fastly.net",
        "cartodb-basemaps-b.global.ssl.fastly.net",
        "cartodb-basemaps-c.global.ssl.fastly.net",
        "cartodb-basemaps-d.global.ssl.fastly.net",
        "basemaps.cartocdn.com"
    ];

    /// <summary>Resolves provider compatibility from both persisted key and normalized endpoint.</summary>
    internal static TileProviderCompatibilityDecision ResolveCompatibility(string? key, string? template)
    {
        var normalizedKey = key?.Trim().ToLowerInvariant() ?? string.Empty;
        if (ThunderforestKeys.Contains(normalizedKey, StringComparer.Ordinal))
            return Blocked("https://www.thunderforest.com/terms/", "This removed Thunderforest provider is incompatible with Wayfarer's cache/proxy architecture.");
        if (CartoKeys.Contains(normalizedKey, StringComparer.Ordinal))
            return Blocked("https://docs.carto.com/faqs/carto-basemaps", "This removed CARTO provider is not available through Wayfarer's cache/proxy architecture.");

        if (!TryCreateTemplateUri(template?.Trim() ?? string.Empty, out var uri, out _))
            return Invalid("The provider endpoint is blank or malformed.");

        var host = NormalizeHost(uri.IdnHost);
        if (MatchesHostSet(host, ThunderforestHosts))
            return Blocked("https://www.thunderforest.com/terms/", "The endpoint is blocked for compatibility.");
        if (MatchesHostSet(host, CartoHosts))
            return Blocked("https://docs.carto.com/faqs/carto-basemaps", "The endpoint is blocked for compatibility.");

        var preset = FindPreset(normalizedKey);
        if (preset != null)
        {
            if (!string.Equals(NormalizeTemplate(template!), NormalizeTemplate(preset.UrlTemplate), StringComparison.Ordinal))
                return Invalid("The selected built-in provider endpoint does not match its maintained preset.");
        }
        else if (!string.Equals(normalizedKey, CustomProviderKey, StringComparison.Ordinal))
        {
            return Invalid("The provider selection is removed or unknown.");
        }

        var source = normalizedKey switch
        {
            ApplicationSettings.DefaultTileProviderKey => "https://operations.osmfoundation.org/policies/tiles/",
            "opentopomap" => "https://services.opentopomap.org/about",
            _ => "Administrator-managed provider agreement"
        };
        var auditSource = normalizedKey switch
        {
            ApplicationSettings.DefaultTileProviderKey => "OSM tile policy",
            "opentopomap" => "OpenTopoMap usage policy",
            _ => "Administrator-managed provider agreement"
        };
        return new(TileProviderCompatibility.Supported, source, auditSource, "Supported.");
    }

    /// <summary>Returns true for an exact maintained host or one of its DNS subdomains.</summary>
    internal static bool IsBlockedContactHost(string? host)
    {
        var normalized = NormalizeHost(host);
        return MatchesHostSet(normalized, ThunderforestHosts) || MatchesHostSet(normalized, CartoHosts);
    }

    private static TileProviderCompatibilityDecision Blocked(string source, string message) =>
        new(
            TileProviderCompatibility.Blocked,
            source,
            source.Contains("thunderforest", StringComparison.OrdinalIgnoreCase)
                ? "Thunderforest terms"
                : "CARTO basemap policy",
            message);

    private static TileProviderCompatibilityDecision Invalid(string message) =>
        new(
            TileProviderCompatibility.InvalidOrUnsupported,
            "Wayfarer compatibility validation",
            "Wayfarer compatibility validation",
            message);

    private static string NormalizeHost(string? host) => (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool MatchesHostSet(string host, IEnumerable<string> blockedHosts) =>
        blockedHosts.Any(blocked => host.Equals(blocked, StringComparison.Ordinal) || host.EndsWith($".{blocked}", StringComparison.Ordinal));

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
    /// Detects OSM Standard from its canonical endpoint rather than an editable provider label.
    /// </summary>
    public static bool IsCanonicalOsmTemplate(string? template) =>
        !string.IsNullOrWhiteSpace(template) &&
        string.Equals(
            NormalizeTemplate(template),
            NormalizeTemplate(ApplicationSettings.DefaultTileProviderUrlTemplate),
            StringComparison.Ordinal);

    /// <summary>
    /// Validates a tile URL template and ensures it points to a HTTPS PNG tile endpoint.
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

        if (!string.IsNullOrEmpty(templateUri.UserInfo))
        {
            error = "Tile URL template must not include user information.";
            return false;
        }

        if (ContainsLiteralCredential(trimmed))
        {
            error = "Credential query parameters must use the {apiKey} placeholder.";
            return false;
        }

        if (!string.Equals(Path.GetExtension(templateUri.AbsolutePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "Tile URL template must point to a .png resource.";
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
            .Replace("{s}", "a", StringComparison.OrdinalIgnoreCase)
            .Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Encode API key before injecting it into query templates.
            var encodedApiKey = Uri.EscapeDataString(apiKey);
            tileUrl = tileUrl.Replace("{apiKey}", encodedApiKey, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Creates the non-secret persistent identity used to isolate cached and in-flight tile work.
    /// API-key placeholders remain symbolic and concrete API-key values are never supplied here.
    /// </summary>
    public static TileProviderCacheIdentity CreateCacheIdentity(string? providerKey, string template)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(providerKey)
            ? ApplicationSettings.DefaultTileProviderKey
            : providerKey.Trim().ToLowerInvariant();
        var normalizedTemplate = NormalizeTemplate(template);
        var input = $"{normalizedKey}|{normalizedTemplate}";
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        var canonicalOsm =
            normalizedKey == ApplicationSettings.DefaultTileProviderKey &&
            normalizedTemplate == NormalizeTemplate(ApplicationSettings.DefaultTileProviderUrlTemplate);
        return new TileProviderCacheIdentity(fingerprint, canonicalOsm);
    }

    /// <summary>
    /// Redacts API key values from a tile URL for safe logging.
    /// Replaces common API key query parameter values with [REDACTED].
    /// </summary>
    /// <param name="url">The tile URL that may contain an API key.</param>
    /// <returns>The URL with API key values redacted.</returns>
    public static string RedactApiKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        // Match common API key parameter patterns: apikey=xxx, api_key=xxx, key=xxx, token=xxx, access_token=xxx
        // Handles both ?param=value and &param=value cases
        return System.Text.RegularExpressions.Regex.Replace(
            url,
            @"([?&])(apikey|api_key|key|token|access_token)=([^&\s]+)",
            "$1$2=[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool ContainsRequiredPlaceholders(string template)
    {
        return template.Contains("{z}", StringComparison.OrdinalIgnoreCase)
               && template.Contains("{x}", StringComparison.OrdinalIgnoreCase)
               && template.Contains("{y}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects concrete values for common credential-bearing query parameters.</summary>
    private static bool ContainsLiteralCredential(string template)
    {
        var queryIndex = template.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return false;
        }

        string[] credentialNames = ["apikey", "api_key", "key", "token", "access_token"];
        foreach (var part in template[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 &&
                credentialNames.Contains(pair[0], StringComparer.OrdinalIgnoreCase) &&
                !pair[1].Equals("{apiKey}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalizes a validated template without expanding credential placeholders.</summary>
    private static string NormalizeTemplate(string template)
    {
        var normalizedPlaceholders = template.Trim()
            .Replace("{S}", "{s}", StringComparison.Ordinal)
            .Replace("{Z}", "{z}", StringComparison.Ordinal)
            .Replace("{X}", "{x}", StringComparison.Ordinal)
            .Replace("{Y}", "{y}", StringComparison.Ordinal)
            .Replace("{APIKEY}", "{apiKey}", StringComparison.OrdinalIgnoreCase);
        var sample = normalizedPlaceholders
            .Replace("{s}", "a", StringComparison.Ordinal)
            .Replace("{z}", "0", StringComparison.Ordinal)
            .Replace("{x}", "0", StringComparison.Ordinal)
            .Replace("{y}", "0", StringComparison.Ordinal)
            .Replace("{apiKey}", "WAYFARER_API_KEY", StringComparison.Ordinal);
        var uri = new Uri(sample);
        var authority = uri.IsDefaultPort
            ? $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.TrimEnd('.').ToLowerInvariant()}"
            : $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.TrimEnd('.').ToLowerInvariant()}:{uri.Port}";
        var normalized = authority + uri.PathAndQuery;
        return normalized.Replace("WAYFARER_API_KEY", "{apiKey}", StringComparison.Ordinal);
    }

    private static bool TryCreateTemplateUri(string template, out Uri uri, out string error)
    {
        var sample = template
            .Replace("{s}", "a", StringComparison.OrdinalIgnoreCase)
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

}

/// <summary>
/// Identifies one provider cache namespace without retaining provider credentials or client identity.
/// </summary>
/// <param name="Fingerprint">SHA-256 fingerprint safe for paths and shared-work keys.</param>
/// <param name="CanAdoptLegacyOsm">Whether unscoped legacy cache entries have proven OSM provenance.</param>
public readonly record struct TileProviderCacheIdentity(
    string Fingerprint,
    bool CanAdoptLegacyOsm);

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
