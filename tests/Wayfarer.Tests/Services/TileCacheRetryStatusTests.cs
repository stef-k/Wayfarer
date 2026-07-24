using System.Net;
using System.Net.Http.Headers;
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
/// Proves the Phase 2A production retry, status, budget, and cancellation contract.
/// </summary>
[Collection("OutboundBudget")]
public sealed partial class TileCacheRetryStatusTests
{
    /// <summary>Proves a confirmed upstream absence is not retried and remains a local 404.</summary>
    [Fact]
    public async Task Upstream404_ReturnsLocal404AfterOneAttempt()
    {
        using var harness = CreateHarness(HttpStatusCode.NotFound);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<NotFoundObjectResult>(await controller.GetTile(5, 1, 1));

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves permanent non-rate-limit client failures are never retried.</summary>
    [Fact]
    public async Task PermanentUpstream4xx_IsNotRetried()
    {
        using var harness = CreateHarness(HttpStatusCode.Forbidden);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 2, 1));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves exhausted upstream server failures are exposed as temporary local failure.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Upstream5xxExhaustion_ReturnsLocal503(HttpStatusCode statusCode)
    {
        using var harness = CreateHarness(statusCode);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 3, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(3, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves every actual retry acquires global capacity independently.</summary>
    [Fact]
    public async Task EachUpstreamAttempt_ConsumesGlobalBudgetToken()
    {
        var acquisitions = 0;
        using var harness = CreateHarness(HttpStatusCode.ServiceUnavailable);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(_ =>
        {
            Interlocked.Increment(ref acquisitions);
            return Task.FromResult(new OutboundBudgetAcquisition(true, TimeSpan.Zero));
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        await controller.GetTile(5, 4, 1);

        Assert.Equal(3, harness.Upstream.Requests.Count);
        Assert.Equal(3, acquisitions);
    }

    /// <summary>Proves caller cancellation propagates instead of becoming a provider response.</summary>
    [Fact]
    public async Task CallerCancellation_PropagatesFromController()
    {
        using var cancellation = new CancellationTokenSource();
        var upstream = new RecordingTileHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.GetTile(5, 5, 1));
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves uncancelled transport timeouts exhaust as local 503 rather than 404.</summary>
    [Fact]
    public async Task TimeoutExhaustion_ReturnsLocal503()
    {
        var upstream = new RecordingTileHandler((_, token) =>
            throw new OperationCanceledException(token));
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(jitter: _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 6, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(3, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves unusable provider delays open a gate and prevent an immediate second request.</summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("999999999999999999999999999999")]
    [InlineData("Wed, 22 Jul 2026 12:00:00 GMT")]
    public async Task UnusableRetryAfter_OpensProviderGate(string retryAfter)
    {
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(
            () => new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var firstScope = harness.CreateScope();
        var firstController = CreateController(firstScope);

        var first = Assert.IsType<ObjectResult>(await firstController.GetTile(5, 9, 1));
        using var secondScope = harness.CreateScope();
        var secondController = CreateController(secondScope);
        var second = Assert.IsType<ObjectResult>(await secondController.GetTile(5, 10, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, second.StatusCode);
        Assert.Single(harness.Upstream.Requests);
        Assert.Contains(harness.Logs.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("unusable Retry-After", StringComparison.Ordinal));
    }

    /// <summary>Proves a valid long provider delay is retained and blocks another request.</summary>
    [Fact]
    public async Task LongProviderDelay_PreventsEarlySecondRequest()
    {
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var firstScope = harness.CreateScope();
        var firstController = CreateController(firstScope);

        var first = Assert.IsType<ObjectResult>(await firstController.GetTile(5, 11, 1));
        using var secondScope = harness.CreateScope();
        var secondController = CreateController(secondScope);
        var second = Assert.IsType<ObjectResult>(await secondController.GetTile(5, 12, 1));

        Assert.Equal("30", firstController.Response.Headers.RetryAfter);
        Assert.Equal("30", secondController.Response.Headers.RetryAfter);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, second.StatusCode);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves final-attempt Retry-After still establishes the provider-wide gate.</summary>
    [Fact]
    public async Task FinalAttemptRetryAfter_IsRetainedForNextRequest()
    {
        var attempts = 0;
        var upstream = new RecordingTileHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(jitter: _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var firstScope = harness.CreateScope();
        var firstController = CreateController(firstScope);

        Assert.IsType<ObjectResult>(await firstController.GetTile(5, 19, 1));
        using var secondScope = harness.CreateScope();
        var secondController = CreateController(secondScope);
        Assert.IsType<ObjectResult>(await secondController.GetTile(5, 20, 1));

        Assert.Equal(3, harness.Upstream.Requests.Count);
        Assert.Equal("30", secondController.Response.Headers.RetryAfter);
    }

    /// <summary>Proves elapsed transport and delay time stop retries at the 45-second ceiling.</summary>
    [Fact]
    public async Task InteractiveDurationCeiling_StopsAnotherAttempt()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var upstream = new RecordingTileHandler((_, _) =>
        {
            now += TimeSpan.FromSeconds(44);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
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

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 21, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(2, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves cancellation during a provider not-before wait stops the retry series.</summary>
    [Fact]
    public async Task ProviderWaitCancellation_StopsBeforeSecondAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileCacheService.SetColdMissRetryDelayForTesting((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.GetTile(5, 13, 1));

        Assert.Single(harness.Upstream.Requests);
        Assert.Single(harness.Logs.Entries, entry =>
            entry.EventId.Id == (int)TileCacheDiagnosticEventIds.Cancellation &&
            Equals(entry.Fields["CancellationStage"], "provider-not-before-wait"));
    }

    /// <summary>Proves a retry denied global capacity never reaches the provider.</summary>
    [Fact]
    public async Task RetryWithoutGlobalCapacity_DoesNotContactProvider()
    {
        var acquisitions = 0;
        using var harness = CreateHarness(HttpStatusCode.InternalServerError);
        TileProviderRetryPolicy.SetDeterminismForTesting(jitter: _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(_ =>
        {
            var acquired = Interlocked.Increment(ref acquisitions) == 1;
            return Task.FromResult(new OutboundBudgetAcquisition(acquired, TimeSpan.Zero));
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 14, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Single(harness.Upstream.Requests);
        Assert.Equal(2, acquisitions);
    }

    /// <summary>Proves retries do not charge the initiating client's allowance repeatedly.</summary>
    [Fact]
    public async Task Retry_ChargesClientAllowanceOnce()
    {
        var attempts = 0;
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(Interlocked.Increment(ref attempts) == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : PngResponse()));
        using var harness = new TileCacheTestHarness(upstream);
        harness.Settings.TileOutboundBudgetPerIpPerMinute = 1;
        TileProviderRetryPolicy.SetDeterminismForTesting(jitter: _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = await controller.GetTile(5, 15, 1);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(2, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves a successful retry writes the tile and the next request is a fresh cache hit.</summary>
    [Fact]
    public async Task SuccessfulRetry_WritesCacheForImmediateFreshHit()
    {
        var attempts = 0;
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(Interlocked.Increment(ref attempts) == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : PngResponse()));
        using var harness = new TileCacheTestHarness(upstream);
        TileProviderRetryPolicy.SetDeterminismForTesting(jitter: _ => 0d);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var firstScope = harness.CreateScope();
        var firstController = CreateController(firstScope);

        Assert.IsType<FileContentResult>(await firstController.GetTile(5, 16, 1));
        using var secondScope = harness.CreateScope();
        var secondController = CreateController(secondScope);
        Assert.IsType<FileContentResult>(await secondController.GetTile(5, 16, 1));

        Assert.Equal(2, harness.Upstream.Requests.Count);
        Assert.True(File.Exists(Path.Combine(harness.CacheDirectory, "5_16_1.png")));
    }

    /// <summary>Proves local request-rate rejection retains 429 with bounded retry guidance.</summary>
    [Fact]
    public async Task LocalRequestRateLimit_Returns429WithRetryAfter()
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileRateLimitEnabled = true;
        harness.Settings.TileRateLimitPerMinute = 1;
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext();
        var controller = CreateController(scope, context);

        Assert.IsType<FileContentResult>(await controller.GetTile(5, 17, 1));
        var rejected = Assert.IsType<ObjectResult>(await controller.GetTile(5, 18, 1));

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Equal(TilesController.BudgetRetryAfterSeconds.ToString(), context.Response.Headers.RetryAfter);
    }

    /// <summary>Creates a fake-only provider harness returning the requested status.</summary>
    private static TileCacheTestHarness CreateHarness(HttpStatusCode statusCode) =>
        new(new RecordingTileHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))));

    /// <summary>Builds deterministic cacheable PNG content.</summary>
    private static HttpResponseMessage PngResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromHours(1)
        };
        return response;
    }

    /// <summary>Creates a controller with a same-origin request and shared cancellation token.</summary>
    private static TilesController CreateController(
        IServiceScope scope,
        CancellationToken cancellationToken = default)
        => CreateController(scope, TileCacheTestHarness.CreateHttpContext(cancellationToken));

    /// <summary>Creates a controller over an explicit test HTTP context.</summary>
    private static TilesController CreateController(
        IServiceScope scope,
        DefaultHttpContext context)
    {
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return new TilesController(
            scope.ServiceProvider.GetRequiredService<ILogger<TilesController>>(),
            scope.ServiceProvider.GetRequiredService<TileCacheService>(),
            scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
