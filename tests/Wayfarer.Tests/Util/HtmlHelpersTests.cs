using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Moq;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// HTML helper extensions for auto-linking text and HTML.
/// </summary>
public class HtmlHelpersTests
{
    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    /// <summary>
    /// Extracts the raw string value from an IHtmlContent without HTML encoding.
    /// Used for testing helpers that return pre-built HtmlString content.
    /// </summary>
    private static string RenderRaw(IHtmlContent content)
    {
        if (content is HtmlString hs)
            return hs.Value ?? string.Empty;

        return Render(content);
    }

    [Fact]
    public void AutoLink_EncodesHtmlAndLinksUrls()
    {
        var helper = Mock.Of<IHtmlHelper>();

        var html = helper.AutoLink("See <b>bold</b> http://example.com");

        var rendered = Render(html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", rendered);
        Assert.Contains("<a href=\"http://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">http://example.com</a>", rendered);
    }

    [Fact]
    public void AutoLink_ReturnsEmptyForNullOrEmpty()
    {
        var helper = Mock.Of<IHtmlHelper>();

        Assert.Equal(HtmlString.Empty.ToString(), Render(helper.AutoLink(null)));
        Assert.Equal(HtmlString.Empty.ToString(), Render(helper.AutoLink("")));
    }

    [Fact]
    public void LinkifyHtml_RespectsExistingTagsAndAddsUrls()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<p>Visit http://example.com and <a href=\"http://already.com\">existing</a>.</p>";

        var html = helper.LinkifyHtml(input);

        var rendered = Render(html);
        Assert.Contains("<a href=\"http://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">http://example.com</a>", rendered);
        Assert.Contains("<a href=\"http://already.com\">existing</a>", rendered);
    }

    #region HasVisibleContent Tests

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("<p></p>", false)]
    [InlineData("<p><br></p>", false)]
    [InlineData("<p><br/></p>", false)]
    [InlineData("<div></div>", false)]
    [InlineData("<p>&nbsp;</p>", false)]
    [InlineData("<p>&#160;</p>", false)]
    [InlineData("<p>  </p>", false)]
    public void HasVisibleContent_ReturnsFalse_ForEmptyContent(string? input, bool expected)
    {
        var result = HtmlHelpers.HasVisibleContent(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("<p>Hello</p>", true)]
    [InlineData("<p>Some <strong>bold</strong> text</p>", true)]
    [InlineData("<div><p>Nested content</p></div>", true)]
    [InlineData("Plain text without tags", true)]
    [InlineData("<p>Text with &nbsp; space</p>", true)]
    [InlineData("<img src='test.jpg' alt='Image'>", false)] // Image without text returns false
    public void HasVisibleContent_ReturnsTrue_ForContentWithText(string input, bool expected)
    {
        var result = HtmlHelpers.HasVisibleContent(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region ProxyNotesImages Tests

    [Fact]
    public void ProxyNotesImages_RewritesExternalImgSrc()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<p>Photo: <img src=\"https://example.com/photo.jpg\" alt=\"pic\"></p>";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        Assert.Contains("/Public/ProxyImage?url=", result);
        Assert.DoesNotContain("src=\"https://example.com/photo.jpg\"", result);
    }

    [Fact]
    public void ProxyNotesImages_LeavesRelativeImagesAlone()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<img src=\"/images/local.jpg\">";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        Assert.Equal(input, result);
    }

    [Fact]
    public void ProxyNotesImages_LeavesDataUrisAlone()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<img src=\"data:image/png;base64,iVBOR\">";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        Assert.Equal(input, result);
    }

    [Fact]
    public void ProxyNotesImages_LeavesAlreadyProxiedAlone()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<img src=\"/Public/ProxyImage?url=https%3A%2F%2Fexample.com%2Fphoto.jpg\">";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        Assert.Equal(input, result);
    }

    [Fact]
    public void ProxyNotesImages_ReturnsEmptyForNull()
    {
        var helper = Mock.Of<IHtmlHelper>();

        var result = RenderRaw(helper.ProxyNotesImages(null));

        Assert.Equal(RenderRaw(HtmlString.Empty), result);
    }

    [Fact]
    public void ProxyNotesImages_PreservesNonImageHtml()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<p>Hello <strong>world</strong></p>";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        Assert.Equal(input, result);
    }

    [Fact]
    public void ProxyNotesImages_HandlesMultipleImages()
    {
        var helper = Mock.Of<IHtmlHelper>();
        var input = "<img src=\"https://a.com/1.jpg\"><img src=\"/local.jpg\"><img src=\"https://b.com/2.png\">";

        var result = RenderRaw(helper.ProxyNotesImages(input));

        // External images should be proxied
        Assert.DoesNotContain("src=\"https://a.com/1.jpg\"", result);
        Assert.DoesNotContain("src=\"https://b.com/2.png\"", result);
        // Local image should remain unchanged
        Assert.Contains("src=\"/local.jpg\"", result);
        // Proxy URLs should be present (2 external images)
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, "/Public/ProxyImage").Count);
    }

    #endregion

    #region ExtractExternalImageUrls Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExtractExternalImageUrls_ReturnsEmpty_ForNullOrEmpty(string? input)
    {
        var result = HtmlHelpers.ExtractExternalImageUrls(input);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractExternalImageUrls_ExtractsHttpsUrls()
    {
        var html = "<p>Photo: <img src=\"https://example.com/photo.jpg\" alt=\"pic\"></p>";

        var result = HtmlHelpers.ExtractExternalImageUrls(html).ToList();

        Assert.Single(result);
        Assert.Equal("https://example.com/photo.jpg", result[0]);
    }

    [Fact]
    public void ExtractExternalImageUrls_ExtractsMultipleUrls()
    {
        var html = "<img src=\"https://a.com/1.jpg\"><img src=\"http://b.com/2.png\">";

        var result = HtmlHelpers.ExtractExternalImageUrls(html).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("https://a.com/1.jpg", result[0]);
        Assert.Equal("http://b.com/2.png", result[1]);
    }

    [Fact]
    public void ExtractExternalImageUrls_IgnoresRelativeAndDataUrls()
    {
        var html = "<img src=\"/local.jpg\"><img src=\"data:image/png;base64,iVBOR\">";

        var result = HtmlHelpers.ExtractExternalImageUrls(html);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractExternalImageUrls_HandlesSingleQuotes()
    {
        var html = "<img src='https://example.com/photo.jpg'/>";

        var result = HtmlHelpers.ExtractExternalImageUrls(html).ToList();

        Assert.Single(result);
        Assert.Equal("https://example.com/photo.jpg", result[0]);
    }

    [Fact]
    public void ExtractExternalImageUrls_HandlesUrlEncodedCharacters()
    {
        var html = "<img src=\"https://example.com/photo%20name.jpg\">";

        var result = HtmlHelpers.ExtractExternalImageUrls(html).ToList();

        Assert.Single(result);
        Assert.Equal("https://example.com/photo%20name.jpg", result[0]);
    }

    #endregion
}
