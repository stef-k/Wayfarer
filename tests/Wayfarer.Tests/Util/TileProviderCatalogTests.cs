using Wayfarer.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for the tile provider catalog validation helpers.
/// </summary>
public class TileProviderCatalogTests
{
    [Fact]
    public void FindPreset_IgnoresCase()
    {
        var preset = TileProviderCatalog.FindPreset(ApplicationSettings.DefaultTileProviderKey.ToUpperInvariant());

        Assert.NotNull(preset);
        Assert.Equal(ApplicationSettings.DefaultTileProviderKey, preset?.Key);
    }

    [Fact]
    public void TryValidateTemplate_ReturnsFalse_WhenMissingPlaceholders()
    {
        var ok = TileProviderCatalog.TryValidateTemplate("https://tiles.example.com/tiles.png", out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidateTemplate_ReturnsFalse_WhenNotHttps()
    {
        var ok = TileProviderCatalog.TryValidateTemplate("http://tiles.example.com/{z}/{x}/{y}.png", out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidateTemplate_ReturnsFalse_WhenNotPng()
    {
        var ok = TileProviderCatalog.TryValidateTemplate("https://tiles.example.com/{z}/{x}/{y}.jpg", out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidateTemplate_AllowsPrivateHost()
    {
        var ok = TileProviderCatalog.TryValidateTemplate("https://192.168.1.10/{z}/{x}/{y}.png", out var error);

        Assert.True(ok);
        Assert.True(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidateTemplate_AllowsSubdomainPlaceholder()
    {
        var ok = TileProviderCatalog.TryValidateTemplate("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", out var error);

        Assert.True(ok);
        Assert.True(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryBuildTileUrl_InsertsCoordinatesAndApiKey()
    {
        var ok = TileProviderCatalog.TryBuildTileUrl(
            "https://tiles.example.com/{z}/{x}/{y}.png?apikey={apiKey}",
            "abc123",
            1,
            2,
            3,
            out var url,
            out var error);

        Assert.True(ok);
        Assert.True(string.IsNullOrWhiteSpace(error));
        Assert.Equal("https://tiles.example.com/1/2/3.png?apikey=abc123", url);
    }

    [Fact]
    public void TryBuildTileUrl_EncodesApiKey()
    {
        var ok = TileProviderCatalog.TryBuildTileUrl(
            "https://tiles.example.com/{z}/{x}/{y}.png?apikey={apiKey}",
            "a+b&c#d",
            1,
            2,
            3,
            out var url,
            out var error);

        Assert.True(ok);
        Assert.True(string.IsNullOrWhiteSpace(error));
        Assert.Equal("https://tiles.example.com/1/2/3.png?apikey=a%2Bb%26c%23d", url);
    }

    [Fact]
    public void TryBuildTileUrl_ReturnsFalse_WhenApiKeyMissing()
    {
        var ok = TileProviderCatalog.TryBuildTileUrl(
            "https://tiles.example.com/{z}/{x}/{y}.png?apikey={apiKey}",
            string.Empty,
            1,
            2,
            3,
            out var url,
            out var error);

        Assert.False(ok);
        Assert.True(string.IsNullOrWhiteSpace(url));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RedactApiKey_RedactsApiKeyParameter()
    {
        var url = "https://tiles.example.com/1/2/3.png?apikey=secret123";

        var result = TileProviderCatalog.RedactApiKey(url);

        Assert.Equal("https://tiles.example.com/1/2/3.png?apikey=[REDACTED]", result);
    }

    [Fact]
    public void RedactApiKey_RedactsMultipleKeyFormats()
    {
        var url = "https://tiles.example.com/tile.png?api_key=secret&token=abc123";

        var result = TileProviderCatalog.RedactApiKey(url);

        Assert.Equal("https://tiles.example.com/tile.png?api_key=[REDACTED]&token=[REDACTED]", result);
    }

    [Fact]
    public void RedactApiKey_IsCaseInsensitive()
    {
        var url = "https://tiles.example.com/tile.png?APIKEY=SECRET&Token=abc";

        var result = TileProviderCatalog.RedactApiKey(url);

        Assert.Equal("https://tiles.example.com/tile.png?APIKEY=[REDACTED]&Token=[REDACTED]", result);
    }

    [Fact]
    public void RedactApiKey_PreservesUrlWithoutApiKey()
    {
        var url = "https://tiles.example.com/1/2/3.png";

        var result = TileProviderCatalog.RedactApiKey(url);

        Assert.Equal(url, result);
    }

    [Fact]
    public void RedactApiKey_ReturnsEmptyForNull()
    {
        var result = TileProviderCatalog.RedactApiKey(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void RedactApiKey_HandlesAccessToken()
    {
        var url = "https://api.mapbox.com/styles/v1/mapbox/streets-v11/tiles/{z}/{x}/{y}?access_token=pk.eyJ1secret";

        var result = TileProviderCatalog.RedactApiKey(url);

        Assert.Equal("https://api.mapbox.com/styles/v1/mapbox/streets-v11/tiles/{z}/{x}/{y}?access_token=[REDACTED]", result);
    }
}
