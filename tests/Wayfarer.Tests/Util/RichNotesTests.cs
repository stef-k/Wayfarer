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
        var output = RichNotes.Normalize($"<a href=\"{url}\">link</a>");
        var anchor = new HtmlParser().ParseDocument(output!).QuerySelector("a")!;
        Assert.Equal(allowed, anchor.HasAttribute("href"));
        Assert.Equal(output, RichNotes.Normalize(output));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("&#9;")]
    [InlineData("\r\n")]
    public void LongControlObfuscationCannotEscapeTheSchemeDecision(string control)
    {
        var url = "java" + string.Concat(Enumerable.Repeat(control, 128)) + "script:window.executed=true";
        var output = RichNotes.Normalize($"<a href=\"{url}\">link</a>");
        Assert.Equal("<a>link</a>", output);
    }

    [Theory]
    [InlineData("<svg><a href='javascript:bad()'>safe</a></svg>")]
    [InlineData("<math><mtext><img src='x' onerror='bad()'>safe</mtext></math>")]
    [InlineData("<form><input autofocus onfocus='bad()'></form><p style='background:url(x)' class='evil'>safe</p>")]
    [InlineData("<object data='x'></object><embed src='x'><style>bad()</style><link href='x'><p>safe</p>")]
    public void ForeignAndActiveResourcesCannotSurvive(string input)
    {
        var output = RichNotes.Normalize(input)!;
        var document = new HtmlParser().ParseDocument(output);
        Assert.Null(document.QuerySelector("svg, math, script, form, input, object, embed, style, link, [onerror], [onfocus], [style]"));
        Assert.DoesNotContain("javascript:", output);
        Assert.Contains("safe", output);
        Assert.Equal(output, RichNotes.Normalize(output));
    }

    [Fact]
    public void NestedProxyAndLiteralEntitiesStabilizeInOnePass()
    {
        var original = "https://cdn.example.test/image?a=1&b=2&literal=&amp;";
        var wrapped = original;
        for (var i = 0; i < 4; i++) wrapped = "https://backend.example.test/Public/ProxyImage?url=" + Uri.EscapeDataString(wrapped);
        var output = RichNotes.Normalize($"<p><img src=\"{wrapped}\"></p>");
        var image = new HtmlParser().ParseDocument(output!).QuerySelector("img")!;
        Assert.Equal(original, image.GetAttribute("src"));
        Assert.Equal(output, RichNotes.Normalize(output));
        var display = ((HtmlString)Mock.Of<IHtmlHelper>().ProxyNotesImages(output)).Value;
        Assert.Equal(output, RichNotes.Normalize(display));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void ProxyDepthHasAStableBound(int depth, bool retained)
    {
        var source = "https://example.test/image.jpg";
        for (var i = 0; i < depth; i++) source = "/Public/ProxyImage?url=" + Uri.EscapeDataString(source);
        var output = RichNotes.Normalize($"<img src=\"{source}\">");
        Assert.Equal(retained, !string.IsNullOrEmpty(output));
        Assert.Equal(output, RichNotes.Normalize(output));
    }

    [Fact]
    public void RichMobileFormattingAndLiteralTextSurviveWhileActiveMarkupIsRemoved()
    {
        const string safe = "<h3>Ελληνικά &amp; 日本語</h3><p class=\"ql-align-justify\"><s>strike</s> &lt;img onerror=alert(1)&gt;</p>"
            + "<ol><li data-list=\"bullet\"><span class=\"ql-font-serif\">list</span></li></ol>"
            + "<p><br></p><p><img src=\"https://example.test/a?x=1&amp;y=2\"></p>";
        var output = RichNotes.Normalize(safe + "<script>alert(1)</script><iframe srcdoc='x'></iframe><p><br></p>");
        Assert.Equal(safe, output);
        Assert.Equal(output, RichNotes.Normalize(output));
        Assert.Equal("<p><br></p>after", RichNotes.Normalize("<p><br></p>after"));
        Assert.Null(RichNotes.Normalize(null));
        Assert.Equal("", RichNotes.Normalize(""));
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
    /// <summary>Exercises the production client formatter in real DOM templates without an application host.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task BrowserNoteImagesTransformBeforeInsertionAndRestoreCanonicalHtml()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var directRequests = 0;
        await page.RouteAsync("https://images.example.test/**", route =>
        {
            Interlocked.Increment(ref directRequests);
            return route.AbortAsync();
        });
        await page.RouteAsync("http://notes.test/**", async route =>
        {
            var script = new Uri(route.Request.Url).AbsolutePath == "/js/util/notes-images.js";
            await route.FulfillAsync(new() { Body = script
                ? await File.ReadAllTextAsync(Path.Combine(root, "wwwroot/js/util/notes-images.js")) : "<main></main>",
                ContentType = script ? "text/javascript" : "text/html" });
        });
        await page.GotoAsync("http://notes.test/");
        const string notes = "<img alt=\"Photo\" src=\"https://images.example.test/photo?a=1&amp;literal=&amp;amp;\"><img src=\"/local.png\"><img src=\"data:image/png;base64,AQI=\">";
        var saved = await page.EvaluateAsync<string>("""
            async html => {
                const module = await import('/js/util/notes-images.js');
                const display = module.notesHtmlForDisplay(html);
                document.querySelector('main').innerHTML = module.notesHtmlForDisplay(display);
                return module.notesHtmlForSave(document.querySelector('main').innerHTML);
            }
            """, notes);
        await page.WaitForFunctionAsync("() => [...document.images].every(image => image.complete)");
        Assert.Equal(notes, saved);
        Assert.Equal("/Public/ProxyImage?url=" + Uri.EscapeDataString("https://images.example.test/photo?a=1&literal=&amp;"),
            await page.Locator("img").Nth(0).GetAttributeAsync("src"));
        Assert.Equal("Photo", await page.Locator("img").Nth(0).GetAttributeAsync("alt"));
        Assert.Equal("/local.png", await page.Locator("img").Nth(1).GetAttributeAsync("src"));
        Assert.Equal("data:image/png;base64,AQI=", await page.Locator("img").Nth(2).GetAttributeAsync("src"));
        Assert.Equal(0, directRequests);
    }

    /// <summary>Real Quill must neutralize unsupported proxy targets before image creation while retaining canonical saves.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task BrowserQuillNoteImagesRejectSchemeRelativeTargetsBeforeInsertion()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var directRequests = 0;
        var proxyRequests = 0;
        await page.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "notes.test")
            {
                Interlocked.Increment(ref directRequests);
                await route.AbortAsync();
                return;
            }
            if (uri.AbsolutePath == "/Public/ProxyImage")
            {
                Interlocked.Increment(ref proxyRequests);
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=") });
                return;
            }
            var script = uri.AbsolutePath.EndsWith(".js");
            await route.FulfillAsync(new() { ContentType = script ? "text/javascript" : "text/html", Body = script
                ? await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", uri.AbsolutePath.TrimStart('/')))
                : "<form><div id='notes'></div><input id='Notes' type='hidden'></form>" });
        });
        await page.GotoAsync("http://notes.test/");
        await page.AddScriptTagAsync(new() { Url = "http://notes.test/lib/quill/quill-2.0.3.js" });
        const string source = "https://images.example.test/photo?a=1&literal=&amp;";
        var result = await page.EvaluateAsync<string[]>("""
            async source => {
                const module = await import('/js/util/notes-images.js');
                module.registerNoteImageBlot(Quill);
                const quill = new Quill('#notes');
                quill.insertEmbed(0, 'image', 'http://notes.test/Public/ProxyImage?url=%2F%2Fimages.example.test%2Fnegative.png');
                quill.insertEmbed(1, 'image', source);
                document.querySelector('#Notes').value = module.notesHtmlForSave(quill.root.innerHTML);
                return [Quill.version, quill.getContents().ops[1].insert.image, document.querySelector('#Notes').value];
            }
            """, source);
        await page.WaitForFunctionAsync("() => [...document.images].every(image => image.complete)");
        Assert.Equal(0, directRequests);
        Assert.Equal(1, proxyRequests);
        Assert.Equal("2.0.3", result[0]);
        Assert.Equal(source, result[1]);
        Assert.Equal("", await page.Locator(".ql-editor img").Nth(0).GetAttributeAsync("src"));
        Assert.Equal("/Public/ProxyImage?url=" + Uri.EscapeDataString(source),
            await page.Locator(".ql-editor img").Nth(1).GetAttributeAsync("src"));
        Assert.Contains("src=\"https://images.example.test/photo?a=1&amp;literal=&amp;amp;\"", result[2]);
        Assert.DoesNotContain("/Public/ProxyImage", result[2]);
        Assert.DoesNotContain("negative.png", result[2]);
    }

    /// <summary>Uses real DOM parsing to keep note-derived and Group description text inert in JS templates.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task BrowserNegative_PopupAndGroupTextCannotBecomeMarkup()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("http://notes.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            var body = path == "/" ? "<table><tbody id='groupsBody'></tbody></table>"
                : path == "/api/groups" ? "[{\"id\":\"group\",\"name\":\"Group\",\"description\":\"<img src=x onerror=window.executed=true>\"}]"
                : await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", path.TrimStart('/')));
            await route.FulfillAsync(new() { Body = body, ContentType = path.EndsWith(".js") ? "text/javascript" : path == "/api/groups" ? "application/json" : "text/html" });
        });
        await page.GotoAsync("http://notes.test/");
        await page.EvaluateAsync("""
            async () => {
                const module = await import('/js/Trip/tripPopupBuilder.js');
                document.body.insertAdjacentHTML('beforeend', module.buildAreaPopup({name:'Area',
                    notes:'&lt;img src=x onerror=window.executed=true&gt;'}));
            }
            """);
        await page.AddScriptTagAsync(new() { Url = "http://notes.test/js/Areas/User/Groups/Index.js" });
        await page.WaitForFunctionAsync("() => document.querySelector('#groupsBody tr') !== null");
        Assert.Equal(0, await page.Locator("img, [onerror]").CountAsync());
        Assert.Contains("<img", await page.Locator(".popup-notes").InnerTextAsync());
        Assert.Contains("<img", await page.Locator("#groupsBody").InnerTextAsync());
        Assert.False(await page.EvaluateAsync<bool>("() => window.executed === true"));
    }

}
