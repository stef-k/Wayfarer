using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Dtos;
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
            "<ul><li>Bullet</li></ul><ol><li>Ordered</li></ol>",
            "<p style=\"text-align: left;\">Left</p>",
            "<p style=\"text-align: center;\">Center</p>",
            "<p style=\"text-align: right;\">Right</p>",
            "<p><span style=\"font-family: serif;\">Serif</span></p>",
            "<p><a href=\"https://example.test/page\" onclick=\"alert(1)\">Link</a></p>",
            "<p><img src=\"https://cdn.example.test/image.jpg\" onerror=\"alert(1)\"></p>");

        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(input);

        Assert.Contains("<strong>Bold</strong>", result);
        Assert.Contains("<em>Italic</em>", result);
        Assert.Contains("<u>Underline</u>", result);
        Assert.Contains("<ul><li>Bullet</li></ul>", result);
        Assert.Contains("<ol><li>Ordered</li></ol>", result);
        Assert.Contains("<p style=\"text-align: left;\">Left</p>", result);
        Assert.Contains("<p style=\"text-align: center;\">Center</p>", result);
        Assert.Contains("<p style=\"text-align: right;\">Right</p>", result);
        Assert.Contains("<span style=\"font-family: serif;\">Serif</span>", result);
        Assert.Contains("href=\"https://example.test/page\"", result);
        Assert.Contains("src=\"https://cdn.example.test/image.jpg\"", result);
        Assert.DoesNotContain("onclick", result);
        Assert.DoesNotContain("onerror", result);
    }

    /// <summary>Retired vendor attributes receive no special interpretation or content deletion.</summary>
    [Fact]
    public void NormalizeForPersistence_StripsAllClassesAndListMetadataWithoutTranslation()
    {
        var result = EditorRichNotesRequestHtml.NormalizeForPersistence(
            "<p class=\"ql-align-right\"><span class=\"ql-font-serif ql-ui\" contenteditable=\"false\">Visible</span></p>"
            + "<ol><li data-list=\"bullet\">Item</li></ol>");

        Assert.Equal("<p><span>Visible</span></p><ol><li>Item</li></ol>", result);
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
    [InlineData("<ol><li>One</li><li><br></li><li><strong> </strong><br></li></ol>", "<ol><li>One</li></ol>")]
    [InlineData("<ul><li>One</li><li>&nbsp;</li></ul>", "<ul><li>One</li></ul>")]
    [InlineData("<ol><li><br></li></ol>", "")]
    [InlineData("<ol><li>One</li><li><br></li><li>Three</li></ol>", "<ol><li>One</li><li><br></li><li>Three</li></ol>")]
    [InlineData("<ol><li><img src=\"https://cdn.example.test/image.jpg\"></li><li><br></li></ol>", "<ol><li><img src=\"https://cdn.example.test/image.jpg\"></li></ol>")]
    [InlineData("<p>Before</p><ol><li>Item</li></ol><p>After</p>", "<p>Before</p><ol><li>Item</li></ol><p>After</p>")]
    [InlineData("<ol><li>One</li><li><br></li></ol><p>After</p>", "<ol><li>One</li><li><br></li></ol><p>After</p>")]
    [InlineData("<ul><li><a href=\"https://example.test\">Visible link</a></li><li><br></li></ul>", "<ul><li><a href=\"https://example.test\">Visible link</a></li></ul>")]
    [InlineData("<p>Before</p><ol><li>Item</li></ol><h2>After</h2>", "<p>Before</p><ol><li>Item</li></ol><h2>After</h2>")]
    [InlineData("<ol><li><video src=\"https://example.test/a.mp4\"></video></li></ol>", "")]
    [InlineData("<ol><li><video>Fallback text</video></li></ol>", "<ol><li>Fallback text</li></ol>")]
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
        const string notesHtml = "<p style=\"text-align: right;\" onclick=\"alert(1)\">Right</p><p><img src=\"/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fimage.jpg\"></p><p><br></p>";
        const string expectedNotesHtml = "<p style=\"text-align: right;\">Right</p><p><img src=\"https://cdn.example.test/image.jpg\"></p>";

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

    /// <summary>All scalar owners retain frozen originals across sanitizer changes, while replacements use current HTML policy.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConditionalParsersKeepExactOriginalNotes(bool web)
    {
        const string original = "<p class=\"ql-align-right\">Original</p>";
        const string desired = "<p style=\"text-align: center\" onclick=\"bad()\">Edited</p>";
        const string normalized = "<p style=\"text-align: center;\">Edited</p>";
        var key = web ? "notesHtml" : "notes";
        // Place and Area additionally require parent evidence; no production owner is substituted.
        MemoryStream Body(bool parent = false)
        {
            var expected = new Dictionary<string, object?> { [key] = original };
            if (parent) expected["regionId"] = Guid.Parse("c4487473-30df-4574-bb19-72e5ba3c84c5");
            return new(JsonSerializer.SerializeToUtf8Bytes(new { expected, changes = new Dictionary<string, string> { [key] = desired } }));
        }
        using var tripBody = Body();
        var trip = await TripMetadataConditionalRequest.ReadAsync(tripBody, web, default);
        Assert.Equal(original, trip.Expected["notes"]); Assert.Equal(normalized, trip.Changes["notes"]);
        using var regionBody = Body();
        var region = await RegionConditionalRequest.ReadAsync(regionBody, web, default);
        Assert.Equal(original, region.Expected["notes"]); Assert.Equal(normalized, region.Changes["notes"]);
        using var placeBody = Body(true);
        var place = await PlaceConditionalRequest.ReadAsync(placeBody, web, new HashSet<string>(), new HashSet<string>(), default);
        Assert.Equal(original, place.Expected["notes"]); Assert.Equal(normalized, place.Changes["notes"]);
        using var areaBody = Body(true);
        var area = await AreaConditionalRequest.ReadAsync(areaBody, web, default);
        Assert.Equal(original, area.Expected["notes"]); Assert.Equal(normalized, area.Changes["notes"]);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string JsonEncodedText(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();
}
