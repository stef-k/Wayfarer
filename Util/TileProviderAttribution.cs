using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Wayfarer.Models;

namespace Wayfarer.Util;

/// <summary>
/// Resolves safe display HTML for the active tile provider attribution.
/// </summary>
public static partial class TileProviderAttribution
{
    /// <summary>
    /// Canonical OpenStreetMap copyright page used when visible attribution names OpenStreetMap.
    /// </summary>
    public const string OpenStreetMapCopyrightUrl = "https://www.openstreetmap.org/copyright";

    /// <summary>
    /// Selects the active provider attribution, normalizes visible OpenStreetMap text, and
    /// sanitizes the final HTML at the output boundary.
    /// </summary>
    /// <param name="settings">The active administrator-configured tile settings.</param>
    /// <returns>Sanitized attribution HTML, or an empty string when attribution cannot be proven.</returns>
    public static string Resolve(ApplicationSettings? settings)
    {
        var providerKey = string.IsNullOrWhiteSpace(settings?.TileProviderKey)
            ? ApplicationSettings.DefaultTileProviderKey
            : settings.TileProviderKey.Trim();
        var preset = TileProviderCatalog.FindPreset(providerKey);
        var configuredAttribution = settings?.TileProviderAttribution;
        var source = string.IsNullOrWhiteSpace(configuredAttribution)
            ? preset?.Attribution
            : configuredAttribution;

        var sanitized = HtmlSanitization.SanitizeAttribution(source);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument($"<body>{sanitized}</body>");
        var body = document.Body;
        if (body == null)
        {
            return string.Empty;
        }

        NormalizeVisibleText(body, document);
        SecureNewTabLinks(body);

        return HtmlSanitization.SanitizeAttribution(body.InnerHtml);
    }

    /// <summary>
    /// Walks parsed text nodes so HTML attributes are never considered attribution text.
    /// </summary>
    private static void NormalizeVisibleText(INode node, IDocument document)
    {
        foreach (var child in node.ChildNodes.ToArray())
        {
            if (child is IText text)
            {
                NormalizeTextNode(text, document);
            }
            else
            {
                NormalizeVisibleText(child, document);
            }
        }
    }

    /// <summary>
    /// Canonicalizes OpenStreetMap text within an existing link or wraps unlinked visible text.
    /// </summary>
    private static void NormalizeTextNode(IText text, IDocument document)
    {
        if (!OpenStreetMapText().IsMatch(text.Data))
        {
            return;
        }

        var containingAnchor = FindContainingAnchor(text);
        if (containingAnchor != null)
        {
            text.Data = OpenStreetMapText().Replace(text.Data, "OpenStreetMap");
            containingAnchor.SetAttribute("href", OpenStreetMapCopyrightUrl);
            containingAnchor.RemoveAttribute("target");
            containingAnchor.RemoveAttribute("rel");
            return;
        }

        var parent = text.Parent;
        if (parent == null)
        {
            return;
        }

        var position = 0;
        foreach (Match match in OpenStreetMapText().Matches(text.Data))
        {
            if (match.Index > position)
            {
                parent.InsertBefore(document.CreateTextNode(text.Data[position..match.Index]), text);
            }

            var link = document.CreateElement("a");
            link.SetAttribute("href", OpenStreetMapCopyrightUrl);
            link.TextContent = "OpenStreetMap";
            parent.InsertBefore(link, text);
            position = match.Index + match.Length;
        }

        if (position < text.Data.Length)
        {
            parent.InsertBefore(document.CreateTextNode(text.Data[position..]), text);
        }

        parent.RemoveChild(text);
    }

    /// <summary>
    /// Finds the nearest containing anchor for a visible text node.
    /// </summary>
    private static IElement? FindContainingAnchor(INode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is IElement element &&
                element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Ensures administrator-supplied new-tab links cannot control the opener page.
    /// </summary>
    private static void SecureNewTabLinks(IElement root)
    {
        foreach (var link in root.QuerySelectorAll("a[target]"))
        {
            if (string.Equals(link.GetAttribute("target"), "_blank", StringComparison.OrdinalIgnoreCase))
            {
                link.SetAttribute("rel", "noopener noreferrer");
            }
        }
    }

    [GeneratedRegex("OpenStreetMap", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenStreetMapText();
}
