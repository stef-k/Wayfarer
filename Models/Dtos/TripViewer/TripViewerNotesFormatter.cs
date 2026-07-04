using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace Wayfarer.Models.Dtos.TripViewer;

/// <summary>
/// Produces display-safe viewer notes payloads from stored rich HTML.
/// </summary>
internal static class TripViewerNotesFormatter
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "blockquote", "br", "em", "h1", "h2", "h3", "h4", "h5", "h6", "img", "li", "ol", "p", "span", "strong", "u", "ul"
    };

    private static readonly HashSet<string> RemovedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "button", "embed", "form", "iframe", "input", "link", "meta", "object", "option", "script", "select", "style", "textarea"
    };

    private static readonly HashSet<string> AllowedAlignmentClasses = new(StringComparer.Ordinal)
    {
        "ql-align-center", "ql-align-right"
    };

    private static readonly HashSet<string> AllowedFontClasses = new(StringComparer.Ordinal)
    {
        "ql-font-monospace", "ql-font-serif"
    };

    private static readonly HashSet<string> QuillBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "li", "p"
    };

    /// <summary>
    /// Sanitizes notes and builds the renderability flags consumed by the viewer.
    /// </summary>
    public static TripViewerNotesDto Format(string? storedHtml)
    {
        if (string.IsNullOrWhiteSpace(storedHtml))
        {
            return Empty();
        }

        var document = Parser.ParseDocument(storedHtml.Trim());
        var body = document.Body;
        if (body == null)
        {
            return Empty();
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

        var displayHtml = body.InnerHtml.Trim();
        if (string.Equals(displayHtml, "<p><br></p>", StringComparison.OrdinalIgnoreCase))
        {
            displayHtml = string.Empty;
        }

        var plainText = NormalizePlainText(body.TextContent ?? string.Empty);
        var hasText = !string.IsNullOrWhiteSpace(plainText);
        var hasMedia = body.QuerySelector("img") != null;
        var hasRenderable = hasText || hasMedia;

        return new TripViewerNotesDto(displayHtml, plainText, hasRenderable, hasText, hasMedia);
    }

    private static TripViewerNotesDto Empty() => new(string.Empty, string.Empty, false, false, false);

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

        if (string.Equals(element.TagName, "a", StringComparison.OrdinalIgnoreCase))
        {
            NormalizeLink(element);
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
            return TryNormalizeAllowedLink(attribute.Value, out _);
        }

        if (name == "src" && string.Equals(element.TagName, "img", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name == "data-list"
            && string.Equals(element.TagName, "li", StringComparison.OrdinalIgnoreCase)
            && (attribute.Value == "bullet" || attribute.Value == "ordered");
    }

    private static bool NormalizeClassAttribute(IElement element)
    {
        var allowed = element.ClassList.Where(className => IsAllowedClass(element, className)).ToArray();
        if (allowed.Length == 0)
        {
            return false;
        }

        element.SetAttribute("class", string.Join(" ", allowed));
        return true;
    }

    private static bool IsAllowedClass(IElement element, string className) =>
        string.Equals(element.TagName, "span", StringComparison.OrdinalIgnoreCase) && AllowedFontClasses.Contains(className)
        || QuillBlockTags.Contains(element.TagName) && AllowedAlignmentClasses.Contains(className);

    private static void NormalizeLink(IElement element)
    {
        if (!TryNormalizeAllowedLink(element.GetAttribute("href") ?? string.Empty, out var href))
        {
            element.RemoveAttribute("href");
        }

        if (element.HasAttribute("href"))
        {
            element.SetAttribute("href", href);
            element.SetAttribute("rel", "noopener noreferrer");
            element.SetAttribute("target", "_blank");
        }
    }

    private static void NormalizeImage(IElement element)
    {
        var source = CanonicalImageSource(element.GetAttribute("src") ?? string.Empty);
        if (!IsAllowedAbsoluteHttpUrl(source))
        {
            element.Remove();
            return;
        }

        element.SetAttribute("src", $"/Public/ProxyImage?url={Uri.EscapeDataString(source)}");
        element.SetAttribute("loading", "lazy");
    }

    private static bool TryNormalizeAllowedLink(string value, out string href) =>
        TryNormalizeAbsoluteHttpUrl(value, out href);

    private static bool IsAllowedAbsoluteHttpUrl(string value) =>
        TryNormalizeAbsoluteHttpUrl(value, out _);

    private static bool TryNormalizeAbsoluteHttpUrl(string value, out string normalized)
    {
        normalized = NormalizeUrlControls(value);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string CanonicalImageSource(string value)
    {
        var trimmed = StripUrlBoundaryControls(WebUtility.HtmlDecode(value));
        if (!Uri.TryCreate(trimmed, UriKind.RelativeOrAbsolute, out var uri))
        {
            return trimmed;
        }

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0];
        if (!string.Equals(path, "/Public/ProxyImage", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var query = uri.IsAbsoluteUri ? uri.Query : new Uri(new Uri("https://wayfarer.local"), uri).Query;
        var values = QueryHelpers.ParseQuery(query);
        return values.TryGetValue("url", out var target) && target.Count > 0
            ? StripUrlBoundaryControls(target[0] ?? trimmed)
            : trimmed;
    }

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
        return string.IsNullOrWhiteSpace(text) && element.QuerySelector("img") == null;
    }

    private static string StripUrlBoundaryControls(string value) =>
        Regex.Replace(value, @"^[\u0000-\u0020\u007f-\u009f]+|[\u0000-\u0020\u007f-\u009f]+$", string.Empty);

    private static string NormalizeUrlControls(string value) =>
        Regex.Replace(StripUrlBoundaryControls(WebUtility.HtmlDecode(value)), @"[\u0000-\u001f\u007f-\u009f]+", string.Empty);

    private static string NormalizePlainText(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value).Replace('\u00a0', ' '), @"\s+", " ").Trim();
}
