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
        var mw = new DynamicRequestSizeMiddleware(next);

        await mw.InvokeAsync(ctx);

        Assert.Equal(100L * 1024 * 1024, feature.MaxRequestBodySize);
        Assert.True(called);
    }

    /// <summary>A read-only limit is safe only when it is present, bounded and no looser than 100 MiB.</summary>
    [Theory]
    [InlineData(false, null, 503)]
    [InlineData(true, null, 503)]
    [InlineData(true, 104857601L, 503)]
    [InlineData(true, 104857600L, 200)]
    [InlineData(true, 1024L, 200)]
    public async Task GlobalCeiling_ReadOnlyFeatureRetainsFailClosedBoundary(bool present, long? limit, int status)
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true, MaxRequestBodySize = limit };
        if (present) context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var called = false;
        await new DynamicRequestSizeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status == 200, called);
        Assert.Equal(limit, feature.MaxRequestBodySize);
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
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; }
    }
}
