using System.Net;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Contains the compact issue #396 blocked-provider pre-contact transport matrix.</summary>
public sealed partial class TileCacheRetryStatusTests
{
    /// <summary>Blocked provider targets are rejected before the fake transport sees an initial contact.</summary>
    [Theory]
    [InlineData("thunderforest-cycle", "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png")]
    [InlineData("carto-dark", "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png")]
    [InlineData("unknown-provider", "https://cartodb-basemaps-a.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png")]
    public async Task BlockedInitialProviderTarget_IsRejectedBeforeTransport(string key, string template)
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileProviderKey = key;
        harness.Settings.TileProviderUrlTemplate = template;
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        await controller.GetTile(5, 18, 1);

        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>Redirects to each blocked provider family stop before the target contact.</summary>
    [Theory]
    [InlineData("https://tile.thunderforest.com/cycle/5/91/1.png")]
    [InlineData("https://basemaps.cartocdn.com/dark_all/5/91/1.png")]
    public async Task BlockedRedirectTarget_IsRejectedBeforeTargetTransport(string target)
    {
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(target);
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        await controller.GetTile(5, 19, 1);

        var contact = Assert.Single(harness.Upstream.Requests);
        Assert.Equal("tile.openstreetmap.org", contact.RequestUri?.Host);
    }

    /// <summary>A DNS-boundary lookalike remains eligible and reaches only the fake transport.</summary>
    [Fact]
    public async Task SafeBlockedProviderLookalike_IsNotFalselyRejected()
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileProviderKey = TileProviderCatalog.CustomProviderKey;
        harness.Settings.TileProviderUrlTemplate =
            "https://tile.thunderforest.com.example.test/{z}/{x}/{y}.png";
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        await controller.GetTile(5, 20, 1);

        var contact = Assert.Single(harness.Upstream.Requests);
        Assert.Equal("tile.thunderforest.com.example.test", contact.RequestUri?.Host);
    }
}
