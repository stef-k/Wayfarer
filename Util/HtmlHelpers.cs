using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
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
            string linked = _urlInTextRegex.Replace(htmlContent, m =>
                $"<a href=\"{m.Value}\" target=\"_blank\" rel=\"noopener noreferrer\">{m.Value}</a>"
            );

            return new HtmlString(linked);
        }
    }
}