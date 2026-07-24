using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Wayfarer.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Verifies the server-authoritative provider-attribution display contract.
/// </summary>
public class TileProviderAttributionTests
{
    private const string OsmCopyrightUrl = "https://www.openstreetmap.org/copyright";

    [Fact]
    public void Resolve_LinksPlainOsmDefaultExactlyOnce()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            ApplicationSettings.DefaultTileProviderKey,
            ApplicationSettings.DefaultTileProviderAttribution));

        Assert.Contains($"href=\"{OsmCopyrightUrl}\"", result);
        Assert.Contains(">OpenStreetMap</a> contributors", result);
        Assert.Equal(1, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_PreservesAlreadyLinkedOsmWithoutDuplication()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            $"&copy; <a href=\"{OsmCopyrightUrl}\">OpenStreetMap</a> contributors"));

        Assert.Equal(1, CountOsmLinks(result));
        Assert.DoesNotContain("<a href=\"https://www.openstreetmap.org/copyright\"><a", result);
    }

    [Fact]
    public void Resolve_PreservesCartoStyleMultiPartyAttribution()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            "carto-positron",
            "&copy; OpenStreetMap contributors &copy; <a href=\"https://carto.com/attributions\">CARTO</a>"));

        Assert.Equal(1, CountOsmLinks(result));
        Assert.Contains(">OpenStreetMap</a> contributors", result);
        Assert.Contains("href=\"https://carto.com/attributions\"", result);
        Assert.Contains(">CARTO</a>", result);
    }

    [Fact]
    public void Resolve_DoesNotAddOsmToCustomNonOsmAttribution()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "&copy; <a href=\"https://tiles.example.com/terms\">Example Maps</a>"));

        Assert.Contains("href=\"https://tiles.example.com/terms\"", result);
        Assert.Contains("Example Maps", result);
        Assert.DoesNotContain("OpenStreetMap", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_LinksOsmInCustomMultiPartyAttribution()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "&copy; openstreetmap contributors | &copy; Example Tiles"));

        Assert.Contains(">OpenStreetMap</a> contributors", result);
        Assert.Contains("Example Tiles", result);
        Assert.Equal(1, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_SanitizesMaliciousStoredAttributionAtOutput()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "<script>alert(1)</script><a href=\"javascript:alert(2)\" onclick=\"evil()\">OpenStreetMap</a>"
            + "<a href=\"data:text/html,evil\">Unsafe</a><strong>Provider</strong>"));

        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<strong>Provider</strong>", result);
        Assert.Equal(1, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_UsesActivePresetWhenStoredAttributionIsMissing()
    {
        var preset = TileProviderCatalog.FindPreset("opentopomap");

        var result = TileProviderAttribution.Resolve(Settings("opentopomap", " "));

        Assert.NotNull(preset);
        Assert.Contains("OpenTopoMap", result);
        Assert.Contains("SRTM", result);
        Assert.Equal(1, CountOsmLinks(result));
    }

    [Theory]
    [InlineData("carto-dark", "CARTO", null)]
    [InlineData("opentopomap", "SRTM", "OpenTopoMap")]
    [InlineData("thunderforest-cycle", "Thunderforest", null)]
    public void Resolve_PreservesEveryPartyInBuiltInPresetAttribution(
        string providerKey,
        string expectedParty,
        string? additionalParty)
    {
        var result = TileProviderAttribution.Resolve(Settings(providerKey, " "));

        Assert.Contains("OpenStreetMap", result);
        Assert.Contains(expectedParty, result);
        if (additionalParty != null)
        {
            Assert.Contains(additionalParty, result);
        }

        Assert.Equal(1, CountOsmLinks(result));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("legacy-unknown")]
    public void Resolve_DoesNotUseOsmFallbackForUnknownOrCustomMissingAttribution(string providerKey)
    {
        var result = TileProviderAttribution.Resolve(Settings(providerKey, " "));

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Resolve_DoesNotRewriteOsmTextInsideAttributes()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "<a href=\"https://example.com/OpenStreetMap\" title=\"OpenStreetMap mirror\">Example Maps</a>"));

        Assert.Contains("href=\"https://example.com/OpenStreetMap\"", result);
        Assert.Contains("title=\"OpenStreetMap mirror\"", result);
        Assert.Equal(0, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_SplitsMixedProviderAnchorWithoutLosingEitherDestination()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "<a href=\"https://example.com/terms\">Example Maps using OpenStreetMap data</a>"));
        var document = new HtmlParser().ParseDocument($"<body>{result}</body>");
        var links = document.Body!.QuerySelectorAll("a");

        Assert.Equal("Example Maps using OpenStreetMap data", document.Body.TextContent);
        Assert.Equal(3, links.Length);
        Assert.Equal("https://example.com/terms", links[0].GetAttribute("href"));
        Assert.Equal("Example Maps using ", links[0].TextContent);
        Assert.Equal(OsmCopyrightUrl, links[1].GetAttribute("href"));
        Assert.Equal("OpenStreetMap", links[1].TextContent);
        Assert.Equal("https://example.com/terms", links[2].GetAttribute("href"));
        Assert.Equal(" data", links[2].TextContent);
        Assert.Empty(document.Body.QuerySelectorAll("a a"));
    }

    [Fact]
    public void Resolve_PreservesSafeLinksAndRepairsMalformedNestedOsmAnchor()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "<a href=\"https://example.com\">Example <a href=\"https://osm.example\">OpenStreetMap</a></a> contributors"));

        Assert.Contains("href=\"https://example.com\"", result);
        Assert.Contains("Example", result);
        Assert.Contains(">OpenStreetMap</a>", result);
        Assert.Equal(1, CountOsmLinks(result));
    }

    [Fact]
    public void Resolve_SecuresAdministratorSuppliedNewTabLinks()
    {
        var result = TileProviderAttribution.Resolve(Settings(
            TileProviderCatalog.CustomProviderKey,
            "<a href=\"https://example.com\" target=\"_BLANK\" rel=\"opener\">Example Maps</a>"));

        Assert.Contains("target=\"_BLANK\"", result);
        Assert.Contains("rel=\"noopener noreferrer\"", result);
        Assert.DoesNotContain("rel=\"opener\"", result);
    }

    private static ApplicationSettings Settings(string providerKey, string attribution) => new()
    {
        TileProviderKey = providerKey,
        TileProviderAttribution = attribution
    };

    private static int CountOsmLinks(string html) =>
        Regex.Matches(
            html,
            $"<a\\s+[^>]*href=\"{Regex.Escape(OsmCopyrightUrl)}\"[^>]*>",
            RegexOptions.IgnoreCase).Count;
}
