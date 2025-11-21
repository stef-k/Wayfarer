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
}
