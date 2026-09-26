using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Wayfarer.Util
{
    public static class HtmlHelpers
    {
        private static readonly Regex _urlRegex = new Regex(
            @"(?<url>https?://[^\s<]+)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        public static IHtmlContent AutoLink(this IHtmlHelper html, string? text)
        {
            if (string.IsNullOrEmpty(text))
                return HtmlString.Empty;

            // Escape any existing HTML
            var encoded = HtmlEncoder.Default.Encode(text);

            // Replace URLs with <a href="…">…</a>
            var linked = _urlRegex.Replace(encoded, match =>
            {
                var url = match.Groups["url"].Value;
                return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>";
            });

            // Return as raw HTML so the <a> tags aren’t escaped again
            return new HtmlString(linked);
        }
        
        /// <summary>
        /// Canonicalizes incoming rich notes and wraps bare http(s):// URLs
        /// in text are wrapped in <a>…</a>.
        /// </summary>
        public static IHtmlContent LinkifyHtml(this IHtmlHelper html, string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return HtmlString.Empty;

            var body = new HtmlParser().ParseDocument(NormalizeNotesForDisplay(htmlContent)).Body!;
            LinkifyText(body);
            return new HtmlString(body.InnerHtml);
        }

        /// <summary>Creates anchors only from text nodes, never from serialized attributes or markup.</summary>
        private static void LinkifyText(INode parent)
        {
            foreach (var node in parent.ChildNodes.ToArray())
            {
                if (node is IElement element && element.LocalName != "a") LinkifyText(element);
                if (node is not IText text) continue;
                var offset = 0;
                foreach (Match match in _urlRegex.Matches(text.Data))
                {
                    parent.InsertBefore(parent.Owner!.CreateTextNode(text.Data[offset..match.Index]), text);
                    var anchor = parent.Owner.CreateElement("a");
                    anchor.SetAttribute("href", match.Value);
                    anchor.SetAttribute("target", "_blank");
                    anchor.SetAttribute("rel", "noopener noreferrer");
                    anchor.TextContent = match.Value;
                    parent.InsertBefore(anchor, text);
                    offset = match.Index + match.Length;
                }
                if (offset == 0) continue;
                parent.InsertBefore(parent.Owner!.CreateTextNode(text.Data[offset..]), text);
                parent.RemoveChild(text);
            }
        }

        /// <summary>
        /// Matches external http(s):// URLs inside &lt;img src="..."&gt; attributes.
        /// </summary>
        private static readonly Regex _externalImgSrcRegex = new Regex(
            @"(<img\b[^>]*?\bsrc\s*=\s*[""'])(?<url>https?://[^""']+)([""'])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Extracts all external http(s) image URLs from &lt;img src="..."&gt; tags in HTML content.
        /// Returns an empty collection for null/empty input.
        /// Shared by <see cref="Wayfarer.Jobs.CacheWarmupJob"/> and view helpers.
        /// </summary>
        public static IEnumerable<string> ExtractExternalImageUrls(string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                yield break;

            foreach (Match match in _externalImgSrcRegex.Matches(htmlContent))
            {
                yield return match.Groups["url"].Value;
            }
        }

        /// <summary>
        /// Rewrites external &lt;img src="https://..."&gt; URLs in HTML content to go through
        /// the /Public/ProxyImage cache endpoint, ensuring consistent caching and SSRF protection.
        /// Injects loading="lazy" on proxied images unless the tag already has a loading attribute.
        /// Canonicalization removes unsupported sources and unwraps existing display proxies first.
        /// </summary>
        public static IHtmlContent ProxyNotesImages(this IHtmlHelper html, string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return HtmlString.Empty;

            var body = new HtmlParser().ParseDocument(NormalizeNotesForDisplay(htmlContent)).Body!;
            foreach (var image in body.QuerySelectorAll("img"))
            {
                image.SetAttribute("src", "/Public/ProxyImage?url=" + Uri.EscapeDataString(image.GetAttribute("src")!));
                image.SetAttribute("loading", "lazy");
            }
            return new HtmlString(body.InnerHtml);
        }

        // Regex to strip HTML tags for content detection
        private static readonly Regex _htmlTagRegex = new Regex(
            @"<[^>]+>",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Checks if HTML content has actual visible text content.
        /// Returns false for null, empty, whitespace-only, or Quill's empty states like &lt;p&gt;&lt;/p&gt; or &lt;p&gt;&lt;br&gt;&lt;/p&gt;.
        /// </summary>
        public static bool HasVisibleContent(string? htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return false;

            var displayHtml = NormalizeNotesForDisplay(htmlContent);
            if (Regex.IsMatch(displayHtml, @"<img\b", RegexOptions.IgnoreCase))
                return true;

            // Strip all HTML tags and check if any visible text remains
            var textOnly = _htmlTagRegex.Replace(displayHtml, "");

            // Also handle HTML entities for whitespace
            textOnly = textOnly
                .Replace("&nbsp;", " ")
                .Replace("&#160;", " ");

            return !string.IsNullOrWhiteSpace(textOnly);
        }

        /// <summary>Publishes canonical safe rich HTML without changing the stored source.</summary>
        public static string NormalizeNotesForDisplay(string? htmlContent) =>
            RichNotes.Normalize(htmlContent) ?? string.Empty;
    }
}
