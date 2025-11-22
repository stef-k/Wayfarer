using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Middleware;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>
/// Lightweight middleware behaviors that can be exercised without a server.
/// </summary>
public class MiddlewareTests
{
    [Fact]
    public async Task DynamicRequestSizeMiddleware_SetsMaxBodySize()
    {
        var feature = new TestMaxRequestBodySizeFeature();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var mw = new DynamicRequestSizeMiddleware(next, 1024);

        await mw.InvokeAsync(ctx);

        Assert.Equal(1024, feature.MaxRequestBodySize);
        Assert.True(called);
    }

    [Fact]
    public async Task PerformanceMonitoringMiddleware_LogsElapsed()
    {
        var logger = new Mock<ILogger<PerformanceMonitoringMiddleware>>();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/health";
        var mw = new PerformanceMonitoringMiddleware(_ => Task.CompletedTask, logger.Object);

        await mw.InvokeAsync(ctx);

        logger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("Request [GET] /health")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private sealed class TestMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
