using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Playwright;
using Moq;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Security and compatibility contracts shared by every rich-note domain.</summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
public sealed class RichNotesTests
{
    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("java&#9;script:alert(1)", false)]
    [InlineData("data:text/html,test", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("file:///tmp/a", false)]
    [InlineData("custom:execute", false)]
    [InlineData("https://example.test/?a=1&amp;b=2", true)]
    [InlineData("http://example.test", true)]
    [InlineData("/relative/path", true)]
    [InlineData("../relative", true)]
    [InlineData("#fragment", true)]
    [InlineData("mailto:user@example.test", true)]
    [InlineData("tel:+1234", true)]
    public void LinkSchemesUseAnExplicitFullValuePolicy(string url, bool allowed)
    {
        var output = RichNotes.NormalizeForPersistence($"<a href=\"{url}\">link</a>");
        var anchor = new HtmlParser().ParseDocument(output!).QuerySelector("a")!;
        Assert.Equal(allowed, anchor.HasAttribute("href"));
        Assert.Equal(output, RichNotes.NormalizeForPersistence(output));
    }

    [Fact]
    public void LongControlObfuscationCannotEscapeTheSchemeDecision()
    {
        var url = "java" + new string('\t', 128) + "script:window.executed=true";
        var output = RichNotes.NormalizeForPersistence($"<a href=\"{url}\">link</a>");
        Assert.Equal("<a>link</a>", output);
    }

    [Fact]
    public void NestedProxyAndLiteralEntitiesStabilizeInOnePass()
    {
        var original = "https://cdn.example.test/image?a=1&b=2&literal=&amp;";
        var wrapped = original;
        for (var i = 0; i < 4; i++) wrapped = "/Public/ProxyImage?url=" + Uri.EscapeDataString(wrapped);
        var output = RichNotes.NormalizeForPersistence($"<p><img src=\"{wrapped}\"></p>");
        var image = new HtmlParser().ParseDocument(output!).QuerySelector("img")!;
        Assert.Equal(original, image.GetAttribute("src"));
        Assert.Equal(output, RichNotes.NormalizeForPersistence(output));
        var display = ((HtmlString)Mock.Of<IHtmlHelper>().ProxyNotesImages(output)).Value;
        Assert.Equal(output, RichNotes.NormalizeForPersistence(display));
    }

    [Fact]
    public void RichMobileFormattingAndLiteralTextSurviveWhileActiveMarkupIsRemoved()
    {
        const string safe = "<h3>Ελληνικά &amp; 日本語</h3><p class=\"ql-align-justify\"><s>strike</s> &lt;img onerror=alert(1)&gt;</p>"
            + "<ol><li data-list=\"bullet\"><span class=\"ql-font-serif\">list</span></li></ol>"
            + "<p><br></p><p><img src=\"https://example.test/a?x=1&amp;y=2\"></p>";
        var output = RichNotes.NormalizeForPersistence(safe + "<script>alert(1)</script><iframe srcdoc='x'></iframe><p><br></p>");
        Assert.Equal(safe, output);
        Assert.Equal(output, RichNotes.NormalizeForPersistence(output));
        Assert.Null(RichNotes.NormalizeForPersistence(null));
        Assert.Equal("", RichNotes.NormalizeForPersistence(""));
    }

    [Fact]
    public void LinkificationCannotCreateAttributesAndDoesNotNestExistingLinks()
    {
        var output = ((HtmlString)Mock.Of<IHtmlHelper>().LinkifyHtml(
            "<p>https://example.test/&quot;onclick=&quot;window.executed=true</p><a href='/safe'>https://other.test</a>")).Value!;
        var document = new HtmlParser().ParseDocument(output);
        Assert.Null(document.QuerySelector("[onclick]"));
        Assert.Equal(2, document.QuerySelectorAll("a").Length);
        Assert.Null(document.QuerySelector("a a"));
    }

    /// <summary>Exercises the two Chromium-confirmed exploits after the actual server presentation transform.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task BrowserNegative_SchemeAndQuotedLinkificationDoNotExecute()
    {
        var input = "<a href=\"java" + new string('\t', 128) + "script:window.executed=true\">click</a>"
            + "<p>https://example.test/&quot;onclick=&quot;window.executed=true</p>";
        var output = ((HtmlString)Mock.Of<IHtmlHelper>().LinkifyHtml(input)).Value!;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<div id='notes'></div>");
        // JSON/string transport followed by innerHTML matches the mobile/timeline consumer boundary.
        await page.EvaluateAsync("html => document.querySelector('#notes').innerHTML = html", output);
        await page.EvaluateAsync("() => document.querySelectorAll('a').forEach(a => { a.dispatchEvent(new MouseEvent('click')); })");
        Assert.False(await page.EvaluateAsync<bool>("() => window.executed === true"));
        Assert.Equal(0, await page.Locator("[onclick], [onerror], script").CountAsync());
        Assert.Null(await page.Locator("a").First.GetAttributeAsync("href"));
    }
}
