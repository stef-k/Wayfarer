using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API SSE stream controller coverage.
/// </summary>
public class SseControllerTests
{
    [Fact]
    public async Task Stream_SetsEventStreamHeaders_AndCompletesOnCancellation()
    {
        var service = new SseService();
        var controller = new SseController(service);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await controller.Stream("trip", "abc", cts.Token);

        Assert.Equal("text/event-stream", context.Response.Headers["Content-Type"].ToString());
        Assert.True(cts.IsCancellationRequested);
    }
}
