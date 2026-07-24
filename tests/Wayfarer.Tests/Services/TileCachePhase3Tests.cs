using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Proves the bounded scheduling and provider-isolation behavior introduced by issue #385 Phase 3.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCachePhase3Tests
{
    /// <summary>Concurrent requests for one provider tile must share one upstream fetch series.</summary>
    [Fact]
    public async Task SameProviderConcurrentColdMisses_ShareOneUpstreamSeries()
    {
        var firstContact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (_, cancellationToken) =>
        {
            firstContact.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PngResponse([1, 2, 3, 4]);
        });
        await using var harness = new TileCacheTestHarness(upstream);

        var first = RequestTileAsync(harness, 5, 7, 8);
        await firstContact.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = RequestTileAsync(harness, 5, 7, 8);
        await Task.Delay(50);
        release.TrySetResult();

        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode));
        Assert.Single(upstream.Requests);
    }

    /// <summary>A provider change must not expose bytes cached for the preceding provider.</summary>
    [Fact]
    public async Task DifferentProviders_DoNotShareCoordinateCache()
    {
        var sequence = 0;
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(PngResponse([(byte)Interlocked.Increment(ref sequence)])));
        await using var harness = new TileCacheTestHarness(upstream);

        var first = await RequestTileAsync(harness, 5, 7, 8);
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        var second = await RequestTileAsync(harness, 5, 7, 8);

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.NotEqual(first.Bytes, second.Bytes);
    }

    /// <summary>Executes one controller request in an independent service scope.</summary>
    private static async Task<LocalTileOutcome> RequestTileAsync(
        TileCacheTestHarness harness,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext(cancellationToken);
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var controller = new TilesController(
            scope.ServiceProvider.GetRequiredService<ILogger<TilesController>>(),
            scope.ServiceProvider.GetRequiredService<TileCacheService>(),
            scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.GetTile(zoom, x, y);
        return result switch
        {
            FileContentResult file => new LocalTileOutcome(StatusCodes.Status200OK, file.FileContents),
            ObjectResult value => new LocalTileOutcome(value.StatusCode ?? StatusCodes.Status200OK, null),
            StatusCodeResult status => new LocalTileOutcome(status.StatusCode, null),
            _ => throw new InvalidOperationException($"Unexpected tile result {result.GetType().Name}.")
        };
    }

    /// <summary>Builds one deterministic cacheable PNG response.</summary>
    private static HttpResponseMessage PngResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Headers.CacheControl =
            new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        return response;
    }

    /// <summary>Captures local status and successful response bytes.</summary>
    private sealed record LocalTileOutcome(int StatusCode, byte[]? Bytes);
}
