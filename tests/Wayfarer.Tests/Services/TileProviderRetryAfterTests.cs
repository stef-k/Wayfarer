using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Contains provider Retry-After parsing and controlled-wait cases for Phase 2A.</summary>
public sealed partial class TileCacheRetryStatusTests
{
    /// <summary>Proves delta-seconds provider guidance is awaited before a successful retry.</summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DeltaRetryAfter_DelaysProviderRetry(HttpStatusCode statusCode)
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var attempts = 0;
        var upstream = new RecordingTileHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var response = new HttpResponseMessage(statusCode);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(response);
            }

            return Task.FromResult(PngResponse());
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(() => now, _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((delay, _) =>
        {
            now += delay;
            return Task.CompletedTask;
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = await controller.GetTile(5, 7, 1);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(2, harness.Upstream.Requests.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 12, 0, 2, TimeSpan.Zero), now);
    }

    /// <summary>Proves HTTP-date provider guidance is parsed and awaited before retry.</summary>
    [Fact]
    public async Task HttpDateRetryAfter_DelaysProviderRetry()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var attempts = 0;
        var upstream = new RecordingTileHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(3));
                return Task.FromResult(response);
            }

            return Task.FromResult(PngResponse());
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(() => now, _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((delay, _) =>
        {
            now += delay;
            return Task.CompletedTask;
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        Assert.IsType<FileContentResult>(await controller.GetTile(5, 8, 1));
        Assert.Equal(2, harness.Upstream.Requests.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 12, 0, 3, TimeSpan.Zero), now);
    }
}
