using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace Wayfarer.Util;

/// <summary>
/// Canonicalizes rich notes for explicit persistence and non-mutating publication boundaries.
/// </summary>
public static class RichNotes
{
    private static readonly Regex DataImageSourceRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*data:image/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "blockquote", "br", "em", "h1", "h2", "h3", "h4", "h5", "h6", "img", "li", "ol", "p", "span", "strong", "u", "ul", "s", "strike", "b", "i"
    };

    private static readonly HashSet<string> RemovedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "button", "embed", "form", "iframe", "input", "link", "meta", "object", "option", "script", "select", "style", "textarea"
    };

    private static readonly HashSet<string> AllowedAlignmentClasses = new(StringComparer.Ordinal)
    {
        "ql-align-center", "ql-align-right", "ql-align-justify"
    };

    private static readonly HashSet<string> AllowedFontClasses = new(StringComparer.Ordinal)
    {
        "ql-font-monospace", "ql-font-serif"
    };

    private static readonly HashSet<string> QuillBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "li", "p"
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
    /// Canonicalizes and sanitizes rich-notes HTML at storage and publication boundaries.
    /// </summary>
    public static string? Normalize(string? notesHtml)
    {
        if (notesHtml is null) return null;
        if (string.IsNullOrWhiteSpace(notesHtml))
        {
            return string.Empty;
        }

        var document = new HtmlParser().ParseDocument(notesHtml.Trim());
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

        RemoveTerminalBlankBlocks(body);
        var html = body.InnerHtml.Trim();
        return string.Equals(html, "<p><br></p>", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : html;
    }

    /// <summary>Derives plain preview text; callers must encode it when placing it into HTML.</summary>
    public static string? ToPlainText(string? notesHtml) => notesHtml is null
        ? null : new HtmlParser().ParseDocument(Normalize(notesHtml)!).Body!.TextContent;

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
        var allowed = element.ClassList.Where(className => IsAllowedClass(element, className)).ToArray();
        if (allowed.Length == 0)
        {
            return false;
        }

        element.SetAttribute("class", string.Join(" ", allowed));
        return true;
    }

    private static bool IsAllowedClass(IElement element, string className) =>
        (string.Equals(element.TagName, "span", StringComparison.OrdinalIgnoreCase) && AllowedFontClasses.Contains(className))
        || (QuillBlockTags.Contains(element.TagName) && AllowedAlignmentClasses.Contains(className));

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
        // HTML parsing has decoded entities. Browsers remove TAB/LF/CR anywhere in URLs.
        var normalized = StripUrlBoundaryControls(value).Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var colon = normalized.IndexOf(':');
        var delimiter = normalized.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (delimiter >= 0 && delimiter < colon)) return true;
        var scheme = normalized[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(StripUrlBoundaryControls(value), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void RemoveTerminalBlankBlocks(IElement body)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var terminal = body.ChildNodes.LastOrDefault(node => node is IElement
                || node is IText text && !string.IsNullOrWhiteSpace(text.Data)) as IElement;
            if (terminal != null
                && string.Equals(terminal.TagName, "p", StringComparison.OrdinalIgnoreCase)
                && IsSemanticallyBlank(terminal))
            {
                terminal.Remove();
                changed = true;
                continue;
            }

            if (terminal == null || (!string.Equals(terminal.TagName, "ol", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(terminal.TagName, "ul", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            while (terminal.LastElementChild != null
                && string.Equals(terminal.LastElementChild.TagName, "li", StringComparison.OrdinalIgnoreCase)
                && IsSemanticallyBlank(terminal.LastElementChild))
            {
                terminal.LastElementChild.Remove();
                changed = true;
            }

            if (!terminal.Children.Any(child => string.Equals(child.TagName, "li", StringComparison.OrdinalIgnoreCase)))
            {
                terminal.Remove();
                changed = true;
            }
        }
    }

    private static bool IsSemanticallyBlank(IElement element)
    {
        var text = (element.TextContent ?? string.Empty).Replace('\u00a0', ' ');
        return string.IsNullOrWhiteSpace(text)
            && element.QuerySelector("img") == null;
    }

    private static string CanonicalImageSource(string value)
    {
        // The DOM already decoded the attribute once; additional HTML decoding corrupts literal entities.
        var current = StripUrlBoundaryControls(value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth <= 32 && seen.Add(current); depth++)
        {
            if (!Uri.TryCreate(current, UriKind.RelativeOrAbsolute, out var uri)
                || !string.Equals(uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0],
                    "/Public/ProxyImage", StringComparison.OrdinalIgnoreCase)) return current;
            var query = uri.IsAbsoluteUri ? uri.Query : new Uri(new Uri("https://wayfarer.local"), uri).Query;
            var values = QueryHelpers.ParseQuery(query);
            if (!values.TryGetValue("url", out var target) || target.Count != 1) return string.Empty;
            current = StripUrlBoundaryControls(target[0] ?? string.Empty);
        }
        return string.Empty;
    }

    private static string StripUrlBoundaryControls(string value) =>
        Regex.Replace(value, @"^[\u0000-\u0020\u007f-\u009f]+|[\u0000-\u0020\u007f-\u009f]+$", string.Empty);

}
