using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Normalizes Trip Editor rich-notes request HTML before editor mutations persist it.
/// </summary>
internal static class EditorRichNotesRequestHtml
{
    private static readonly Regex DataImageSourceRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*data:image/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ImageSourceRegex = new(
        @"(?<prefix><img\b[^>]*?\bsrc\s*=\s*[""'])(?<url>[^""']+)(?<suffix>[""'])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HtmlParser Parser = new();
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "blockquote", "br", "em", "h1", "h2", "h3", "h4", "h5", "h6", "img", "li", "ol", "p", "span", "strong", "u", "ul"
    };

    private static readonly HashSet<string> RemovedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "button", "embed", "form", "iframe", "input", "link", "meta", "object", "option", "script", "select", "style", "textarea"
    };

    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal)
    {
        "ql-align-center", "ql-align-right", "ql-font-monospace", "ql-font-serif"
    };

    private static readonly HashSet<string> AllowedListKinds = new(StringComparer.Ordinal)
    {
        "bullet", "ordered"
    };

    /// <summary>
    /// Returns true when request HTML contains a direct embedded data image source.
    /// </summary>
    public static bool ContainsDataImageSource(string? notesHtml) =>
        !string.IsNullOrEmpty(notesHtml) && DataImageSourceRegex.IsMatch(notesHtml);

    /// <summary>
    /// Canonicalizes and sanitizes rich-notes HTML accepted by Trip Editor mutation requests.
    /// </summary>
    public static string? NormalizeForPersistence(string? notesHtml)
    {
        if (string.IsNullOrWhiteSpace(notesHtml))
        {
            return string.Empty;
        }

        var canonicalImages = ImageSourceRegex.Replace(notesHtml.Trim(), match =>
        {
            var source = CanonicalImageSource(match.Groups["url"].Value);
            return $"{match.Groups["prefix"].Value}{WebUtility.HtmlEncode(source)}{match.Groups["suffix"].Value}";
        });

        var document = Parser.ParseDocument(canonicalImages);
        var body = document.Body;
        if (body == null)
        {
            return string.Empty;
        }

        foreach (var element in body.QuerySelectorAll("span.ql-ui").ToArray())
        {
            element.Remove();
        }

        foreach (var element in body.QuerySelectorAll("*").Reverse().ToArray())
        {
            NormalizeElement(element);
        }

        RemoveTrailingBlankParagraphs(body);
        var html = body.InnerHtml.Trim();
        return string.Equals(html, "<p><br></p>", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : html;
    }

    private static void NormalizeElement(IElement element)
    {
        if (RemovedTags.Contains(element.TagName))
        {
            element.Remove();
            return;
        }

        if (!AllowedTags.Contains(element.TagName))
        {
            element.Replace(element.ChildNodes.ToArray());
            return;
        }

        foreach (var attribute in element.Attributes.ToArray())
        {
            if (!IsAllowedAttribute(element, attribute))
            {
                element.RemoveAttribute(attribute.Name);
            }
        }

        if (string.Equals(element.TagName, "img", StringComparison.OrdinalIgnoreCase))
        {
            NormalizeImage(element);
        }
    }

    private static bool IsAllowedAttribute(IElement element, IAttr attribute)
    {
        var name = attribute.Name.ToLowerInvariant();
        if (name == "class")
        {
            return NormalizeClassAttribute(element);
        }

        if (name == "href" && string.Equals(element.TagName, "a", StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedLink(attribute.Value);
        }

        if (name == "src" && string.Equals(element.TagName, "img", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name == "data-list"
            && string.Equals(element.TagName, "li", StringComparison.OrdinalIgnoreCase)
            && AllowedListKinds.Contains(attribute.Value);
    }

    private static bool NormalizeClassAttribute(IElement element)
    {
        var allowed = element.ClassList.Where(className => AllowedClasses.Contains(className)).ToArray();
        if (allowed.Length == 0)
        {
            return false;
        }

        element.SetAttribute("class", string.Join(" ", allowed));
        return true;
    }

    private static void NormalizeImage(IElement element)
    {
        var source = CanonicalImageSource(element.GetAttribute("src") ?? string.Empty);
        if (!IsAllowedAbsoluteHttpUrl(source))
        {
            element.Remove();
            return;
        }

        element.SetAttribute("src", source);
    }

    private static bool IsAllowedLink(string value)
    {
        var compact = CompactUrlScheme(value);
        return !compact.StartsWith("javascript:", StringComparison.Ordinal)
            && !compact.StartsWith("data:", StringComparison.Ordinal)
            && !compact.StartsWith("vbscript:", StringComparison.Ordinal);
    }

    private static bool IsAllowedAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(StripUrlBoundaryControls(value), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void RemoveTrailingBlankParagraphs(IElement body)
    {
        while (body.LastElementChild != null
            && string.Equals(body.LastElementChild.TagName, "p", StringComparison.OrdinalIgnoreCase)
            && IsBlankParagraph(body.LastElementChild))
        {
            body.LastElementChild.Remove();
        }
    }

    private static bool IsBlankParagraph(IElement element)
    {
        var text = (element.TextContent ?? string.Empty).Replace('\u00a0', ' ');
        return string.IsNullOrWhiteSpace(text)
            && element.QuerySelector("img") == null
            && element.QuerySelector("video") == null
            && element.QuerySelector("iframe") == null;
    }

    private static string CanonicalImageSource(string value)
    {
        var trimmed = StripUrlBoundaryControls(WebUtility.HtmlDecode(value));
        if (!Uri.TryCreate(trimmed, UriKind.RelativeOrAbsolute, out var uri)
            || !string.Equals(uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0], "/Public/ProxyImage", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var query = uri.IsAbsoluteUri ? uri.Query : new Uri(new Uri("https://wayfarer.local"), uri).Query;
        var values = QueryHelpers.ParseQuery(query);
        return values.TryGetValue("url", out var target) && target.Count > 0
            ? StripUrlBoundaryControls(target[0] ?? trimmed)
            : trimmed;
    }

    private static string StripUrlBoundaryControls(string value) =>
        Regex.Replace(value, @"^[\u0000-\u0020\u007f-\u009f]+|[\u0000-\u0020\u007f-\u009f]+$", string.Empty);

    private static string CompactUrlScheme(string value) =>
        Regex.Replace(StripUrlBoundaryControls(value)[..Math.Min(64, StripUrlBoundaryControls(value).Length)], @"[\u0000-\u0020\u007f-\u009f]+", string.Empty).ToLowerInvariant();
}
