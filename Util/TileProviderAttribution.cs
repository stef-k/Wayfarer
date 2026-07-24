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
                NormalizeUnlinkedTextNode(text, document);
            }
            else if (child is IElement element &&
                     element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                NormalizeAnchor(element, document);
            }
            else
            {
                NormalizeVisibleText(child, document);
            }
        }
    }

    /// <summary>
    /// Canonicalizes an OpenStreetMap-only link or separates OSM text from a mixed provider link.
    /// </summary>
    private static void NormalizeAnchor(IElement anchor, IDocument document)
    {
        if (!OpenStreetMapText().IsMatch(anchor.TextContent))
        {
            return;
        }

        var visibleText = anchor.TextContent.Trim();
        if (OpenStreetMapOnlyRemainder().IsMatch(
                OpenStreetMapText().Replace(visibleText, string.Empty)))
        {
            NormalizeOsmText(anchor);
            anchor.SetAttribute("href", OpenStreetMapCopyrightUrl);
            anchor.RemoveAttribute("target");
            anchor.RemoveAttribute("rel");
            return;
        }

        var parent = anchor.Parent;
        if (parent == null)
        {
            return;
        }

        foreach (var segment in SplitAttributionNodes(anchor.ChildNodes.ToArray(), document))
        {
            var link = segment.IsOpenStreetMap
                ? document.CreateElement("a")
                : CloneElementShallow(anchor, document);
            if (segment.IsOpenStreetMap)
            {
                link.SetAttribute("href", OpenStreetMapCopyrightUrl);
            }

            foreach (var segmentNode in segment.Nodes)
            {
                link.AppendChild(segmentNode);
            }

            parent.InsertBefore(link, anchor);
        }

        parent.RemoveChild(anchor);
    }

    /// <summary>
    /// Wraps unlinked visible OpenStreetMap text in the canonical copyright link.
    /// </summary>
    private static void NormalizeUnlinkedTextNode(IText text, IDocument document)
    {
        if (!OpenStreetMapText().IsMatch(text.Data))
        {
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
    /// Splits sanitized anchor descendants into provider and canonical OSM segments.
    /// </summary>
    private static List<AttributionSegment> SplitAttributionNodes(
        IEnumerable<INode> nodes,
        IDocument document)
    {
        var segments = new List<AttributionSegment>();
        foreach (var node in nodes)
        {
            if (node is IText text)
            {
                var position = 0;
                foreach (Match match in OpenStreetMapText().Matches(text.Data))
                {
                    if (match.Index > position)
                    {
                        AddSegment(
                            segments,
                            false,
                            document.CreateTextNode(text.Data[position..match.Index]));
                    }

                    AddSegment(segments, true, document.CreateTextNode("OpenStreetMap"));
                    position = match.Index + match.Length;
                }

                if (position < text.Data.Length)
                {
                    AddSegment(segments, false, document.CreateTextNode(text.Data[position..]));
                }
            }
            else if (node is IElement element)
            {
                var childSegments = SplitAttributionNodes(element.ChildNodes.ToArray(), document);
                if (childSegments.Count == 0)
                {
                    AddSegment(segments, false, CloneElementShallow(element, document));
                }
                else
                {
                    foreach (var childSegment in childSegments)
                    {
                        var elementClone = CloneElementShallow(element, document);
                        foreach (var childNode in childSegment.Nodes)
                        {
                            elementClone.AppendChild(childNode);
                        }

                        AddSegment(segments, childSegment.IsOpenStreetMap, elementClone);
                    }
                }
            }
            else
            {
                AddSegment(segments, false, node.Clone(true));
            }
        }

        return segments;
    }

    /// <summary>
    /// Coalesces adjacent nodes that retain the same attribution destination.
    /// </summary>
    private static void AddSegment(
        ICollection<AttributionSegment> segments,
        bool isOpenStreetMap,
        INode node)
    {
        var last = segments.LastOrDefault();
        if (last != null && last.IsOpenStreetMap == isOpenStreetMap)
        {
            last.Nodes.Add(node);
            return;
        }

        segments.Add(new AttributionSegment(isOpenStreetMap, [node]));
    }

    /// <summary>
    /// Copies a sanitized element and its allowed attributes without copying descendants.
    /// </summary>
    private static IElement CloneElementShallow(IElement source, IDocument document)
    {
        var clone = document.CreateElement(source.LocalName);
        foreach (var attribute in source.Attributes)
        {
            clone.SetAttribute(attribute.Name, attribute.Value);
        }

        return clone;
    }

    /// <summary>
    /// Normalizes visible OpenStreetMap casing inside an OSM-only anchor.
    /// </summary>
    private static void NormalizeOsmText(INode node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                text.Data = OpenStreetMapText().Replace(text.Data, "OpenStreetMap");
            }
            else
            {
                NormalizeOsmText(child);
            }
        }
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

    [GeneratedRegex(
        @"^\s*(?:©\s*)?(?:contributors?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenStreetMapOnlyRemainder();

    /// <summary>
    /// Represents one contiguous portion of a mixed attribution anchor.
    /// </summary>
    private sealed record AttributionSegment(bool IsOpenStreetMap, List<INode> Nodes);
}
