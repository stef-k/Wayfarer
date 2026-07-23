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
/// Proves the Phase 2A production retry, status, budget, and cancellation contract.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCacheRetryStatusTests
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

        await controller.GetTile(5, 2, 1);

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

    /// <summary>Creates a fake-only provider harness returning the requested status.</summary>
    private static TileCacheTestHarness CreateHarness(HttpStatusCode statusCode) =>
        new(new RecordingTileHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))));

    /// <summary>Creates a controller with a same-origin request and shared cancellation token.</summary>
    private static TilesController CreateController(
        IServiceScope scope,
        CancellationToken cancellationToken = default)
    {
        var context = TileCacheTestHarness.CreateHttpContext(cancellationToken);
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
