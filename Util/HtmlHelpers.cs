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
        private static readonly HtmlParser _htmlParser = new();
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
        
        // only match URLs in the rendered HTML, not inside existing tags/attributes
        private static readonly Regex _urlInTextRegex = new Regex(
            @"(?<![""'>])\bhttps?://[^\s<]+", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );
        
        /// <summary>
        /// Leaves the incoming HTML alone except that any bare http(s):// URLs
        /// in text are wrapped in <a>…</a>.
        /// </summary>
        public static IHtmlContent LinkifyHtml(this IHtmlHelper html, string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return HtmlString.Empty;

            // run regex replace _on the raw HTML_ so existing tags stay intact
            string linked = _urlInTextRegex.Replace(NormalizeNotesForDisplay(htmlContent), m =>
                $"<a href=\"{m.Value}\" target=\"_blank\" rel=\"noopener noreferrer\">{m.Value}</a>"
            );

            return new HtmlString(linked);
        }

        /// <summary>
        /// Detects an existing loading attribute (e.g. loading="eager") on an &lt;img&gt; tag.
        /// Uses word boundary to avoid false positives from class names like "downloading".
        /// </summary>
        private static readonly Regex _loadingAttrRegex = new Regex(
            @"\bloading\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        /// Leaves relative, data-URI, and already-proxied URLs unchanged.
        /// </summary>
        public static IHtmlContent ProxyNotesImages(this IHtmlHelper html, string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return HtmlString.Empty;

            var displayHtml = NormalizeNotesForDisplay(htmlContent);
            var result = _externalImgSrcRegex.Replace(displayHtml, m =>
            {
                var prefix = m.Groups[1].Value;
                var url = m.Groups["url"].Value;
                var suffix = m.Groups[3].Value;
                var encoded = System.Net.WebUtility.UrlEncode(url);
                var proxied = $"{prefix}/Public/ProxyImage?url={encoded}{suffix}";

                // Inject loading="lazy" unless the tag already has a loading attribute
                var hasLoading = _loadingAttrRegex.IsMatch(prefix);
                if (!hasLoading)
                {
                    var afterMatch = displayHtml.AsSpan(m.Index + m.Length);
                    var closingBracket = afterMatch.IndexOf('>');
                    if (closingBracket >= 0)
                        hasLoading = _loadingAttrRegex.IsMatch(
                            afterMatch[..closingBracket].ToString());
                }

                if (!hasLoading)
                    proxied += " loading=\"lazy\"";

                return proxied;
            });

            return new HtmlString(result);
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

        /// <summary>
        /// Removes only semantically blank terminal editor artifacts for legacy display.
        /// Stored HTML remains unchanged and this method is not a general sanitizer.
        /// </summary>
        public static string NormalizeNotesForDisplay(string? htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return string.Empty;

            var body = _htmlParser.ParseDocument(htmlContent).Body;
            if (body == null)
                return string.Empty;

            var changed = false;
            var passChanged = true;
            while (passChanged)
            {
                passChanged = false;
                var terminal = body.LastElementChild;
                if (terminal != null && terminal.LocalName == "p" && IsSemanticallyBlank(terminal))
                {
                    terminal.Remove();
                    changed = true;
                    passChanged = true;
                    continue;
                }

                if (terminal == null || (terminal.LocalName != "ol" && terminal.LocalName != "ul"))
                    continue;

                while (terminal.LastElementChild?.LocalName == "li" && IsSemanticallyBlank(terminal.LastElementChild))
                {
                    terminal.LastElementChild.Remove();
                    changed = true;
                    passChanged = true;
                }

                if (!terminal.Children.Any(child => child.LocalName == "li"))
                {
                    terminal.Remove();
                    changed = true;
                    passChanged = true;
                }
            }

            return changed ? body.InnerHtml.Trim() : htmlContent.Trim();
        }

        private static bool IsSemanticallyBlank(IElement element) =>
            string.IsNullOrWhiteSpace((element.TextContent ?? string.Empty).Replace('\u00a0', ' '))
            && element.QuerySelector("img") == null;
    }
}
