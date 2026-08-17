using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Contract tests for Trip Editor rich-notes request HTML before persistence.
/// </summary>
public sealed class EditorRichNotesRequestHtmlTests
{
    [Fact]
    public void NormalizeForPersistence_PreservesAllowedFormattingListsAlignmentLinksAndImages()
    {
        var input = string.Concat(
            "<p><strong>Bold</strong> <em>Italic</em> <u>Underline</u></p>",
            "<ol><li data-list=\"bullet\"><span class=\"ql-ui\" contenteditable=\"false\"></span>Bullet</li>",
            "<li data-list=\"ordered\">Ordered</li></ol>",
            "<p class=\"ql-align-left\">Left</p>",
            "<p class=\"ql-align-center\">Center</p>",
            "<p class=\"ql-align-right\">Right</p>",
            "<p><span class=\"ql-font-serif\">Serif</span></p>",
            "<p><a href=\"https://example.test/page\" onclick=\"alert(1)\">Link</a></p>",
            "<p><img src=\"https://cdn.example.test/image.jpg\" onerror=\"alert(1)\"></p>");

        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(input);

        Assert.Contains("<strong>Bold</strong>", result);
        Assert.Contains("<em>Italic</em>", result);
        Assert.Contains("<u>Underline</u>", result);
        Assert.Contains("data-list=\"bullet\"", result);
        Assert.Contains("data-list=\"ordered\"", result);
        Assert.Contains("<p>Left</p>", result);
        Assert.Contains("<p class=\"ql-align-center\">Center</p>", result);
        Assert.Contains("<p class=\"ql-align-right\">Right</p>", result);
        Assert.Contains("<span class=\"ql-font-serif\">Serif</span>", result);
        Assert.Contains("href=\"https://example.test/page\"", result);
        Assert.Contains("src=\"https://cdn.example.test/image.jpg\"", result);
        Assert.DoesNotContain("ql-align-left", result);
        Assert.DoesNotContain("ql-ui", result);
        Assert.DoesNotContain("onclick", result);
        Assert.DoesNotContain("onerror", result);
    }

    [Fact]
    public void NormalizeForPersistence_StripsQuillClassesFromUnsupportedElements()
    {
        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(
            "<p><span class=\"ql-align-right\">Inline alignment</span></p><p class=\"ql-font-serif\">Block font</p>");

        Assert.Contains("<span>Inline alignment</span>", result);
        Assert.Contains("<p>Block font</p>", result);
        Assert.DoesNotContain("ql-align-right", result);
        Assert.DoesNotContain("ql-font-serif", result);
    }

    [Fact]
    public void NormalizeForPersistence_UnwrapsProxyImageUrlsBeforeSave()
    {
        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(
            "<p><img src=\"/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fproxied.jpg\"></p>");

        Assert.Contains("src=\"https://cdn.example.test/proxied.jpg\"", result);
        Assert.DoesNotContain("/Public/ProxyImage", result);
    }

    [Fact]
    public void NormalizeForPersistence_StripsDataImagesUnsafeUrlsAndScripts()
    {
        var input = string.Concat(
            "<script>alert(1)</script>",
            "<p onclick=\"alert(2)\">Safe text</p>",
            "<p><a href=\"javascript:alert(3)\">Unsafe link</a></p>",
            "<p><img src=\"data:image/png;base64,abc\"></p>",
            "<p><img src=\"vbscript:msgbox(1)\"></p>");

        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(input);

        Assert.Contains("Safe text", result);
        Assert.Contains("Unsafe link", result);
        Assert.DoesNotContain("<script", result);
        Assert.DoesNotContain("onclick", result);
        Assert.DoesNotContain("javascript:", result);
        Assert.DoesNotContain("data:image", result);
        Assert.DoesNotContain("<img", result);
        Assert.DoesNotContain("vbscript:", result);
    }

    [Theory]
    [InlineData("<p><br></p>", "")]
    [InlineData("<p>Real content</p><p><br></p><p> </p>", "<p>Real content</p>")]
    [InlineData("<p><img src=\"https://cdn.example.test/image.jpg\"></p><p><br></p>", "<p><img src=\"https://cdn.example.test/image.jpg\"></p>")]
    public void NormalizeForPersistence_RemovesOnlyTrailingHelperBlankParagraphs(string input, string expected)
    {
        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("<ol><li data-list=\"ordered\">One</li><li data-list=\"ordered\"><br></li><li data-list=\"ordered\"><strong> </strong><br></li></ol>", "<ol><li data-list=\"ordered\">One</li></ol>")]
    [InlineData("<ul><li data-list=\"bullet\">One</li><li data-list=\"bullet\">&nbsp;</li></ul>", "<ul><li data-list=\"bullet\">One</li></ul>")]
    [InlineData("<ol><li data-list=\"ordered\"><br></li></ol>", "")]
    [InlineData("<ol><li data-list=\"ordered\">One</li><li data-list=\"ordered\"><br></li><li data-list=\"ordered\">Three</li></ol>", "<ol><li data-list=\"ordered\">One</li><li data-list=\"ordered\"><br></li><li data-list=\"ordered\">Three</li></ol>")]
    [InlineData("<ol><li data-list=\"ordered\"><img src=\"https://cdn.example.test/image.jpg\"></li><li data-list=\"ordered\"><br></li></ol>", "<ol><li data-list=\"ordered\"><img src=\"https://cdn.example.test/image.jpg\"></li></ol>")]
    [InlineData("<p>Before</p><ol><li data-list=\"ordered\">Item</li></ol><p>After</p>", "<p>Before</p><ol><li data-list=\"ordered\">Item</li></ol><p>After</p>")]
    [InlineData("<ol><li data-list=\"ordered\">One</li><li data-list=\"ordered\"><br></li></ol><p>After</p>", "<ol><li data-list=\"ordered\">One</li><li data-list=\"ordered\"><br></li></ol><p>After</p>")]
    [InlineData("<ul><li data-list=\"bullet\"><a href=\"https://example.test\">Visible link</a></li><li data-list=\"bullet\"><br></li></ul>", "<ul><li data-list=\"bullet\"><a href=\"https://example.test\">Visible link</a></li></ul>")]
    [InlineData("<p>Before</p><ol><li data-list=\"ordered\">Item</li></ol><h2>After</h2>", "<p>Before</p><ol><li data-list=\"ordered\">Item</li></ol><h2>After</h2>")]
    public void NormalizeForPersistence_RemovesOnlySemanticallyBlankTerminalListItems(string input, string expected)
    {
        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ContainsDataImageSource_DetectsDirectDataImageBeforeNormalization()
    {
        Assert.True(EditorRichNotesRequestHtml.ContainsDataImageSource("<p><img src=\" DATA:image/png;base64,abc\"></p>"));
        Assert.False(EditorRichNotesRequestHtml.ContainsDataImageSource("<p><img src=\"https://cdn.example.test/image.jpg\"></p>"));
    }

    [Fact]
    public void SaveRequestParsersNormalizeRichNotesBeforeMutationServicesPersist()
    {
        const string notesHtml = "<p class=\"ql-align-right\" onclick=\"alert(1)\">Right</p><p><img src=\"/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fimage.jpg\"></p><p><br></p>";
        const string expectedNotesHtml = "<p class=\"ql-align-right\">Right</p><p><img src=\"https://cdn.example.test/image.jpg\"></p>";

        Assert.True(EditorTripMetadataUpdateRequestParser.TryParse(Json($$"""
        {
          "name": "Trip",
          "notesHtml": "{{JsonEncodedText(notesHtml)}}",
          "isPublic": false,
          "coverImage": null,
          "center": null,
          "zoom": null
        }
        """), out var metadata, out _));
        Assert.Equal(expectedNotesHtml, metadata.NotesHtml);

        Assert.True(EditorRegionRequestParser.TryParseSave(Json($$"""
        {
          "name": "Region",
          "notesHtml": "{{JsonEncodedText(notesHtml)}}",
          "coverImage": null,
          "center": null
        }
        """), out var region, out _));
        Assert.Equal(expectedNotesHtml, region.NotesHtml);

        Assert.True(EditorPlaceRequestParser.TryParseCreate(Json($$"""
        {
          "name": "Place",
          "notesHtml": "{{JsonEncodedText(notesHtml)}}",
          "address": null,
          "location": null,
          "iconName": "marker",
          "markerColor": "bg-blue",
          "reverseGeocode": false
        }
        """), new HashSet<string> { "marker" }, new HashSet<string> { "bg-blue" }, out var place, out _));
        Assert.Equal(expectedNotesHtml, place.NotesHtml);

        Assert.True(EditorAreaRequestParser.TryParseCreate(Json($$"""
        {
          "name": "Area",
          "notesHtml": "{{JsonEncodedText(notesHtml)}}",
          "fillHex": "#ff6600",
          "geometry": { "type": "Polygon", "coordinates": [[[23,37],[24,37],[24,38],[23,37]]] }
        }
        """), out var area, out _));
        Assert.Equal(expectedNotesHtml, area.NotesHtml);

        Assert.True(EditorSegmentRequestParser.TryParseSave(Json($$"""
        {
          "fromPlaceId": null,
          "toPlaceId": null,
          "waypointPlaceIds": [],
          "waypointRouteVertexIndices": [],
          "mode": "walk",
          "estimatedDistanceKm": null,
          "estimatedDurationMinutes": null,
          "estimatedDurationSource": "Automatic",
          "notesHtml": "{{JsonEncodedText(notesHtml)}}",
          "route": null,
          "aggregateConcurrencyToken": null
        }
        """), out var segment, out _));
        Assert.Equal(expectedNotesHtml, segment.NotesHtml);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string JsonEncodedText(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();
}
