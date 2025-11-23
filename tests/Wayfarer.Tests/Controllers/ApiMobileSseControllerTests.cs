using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Mobile SSE controller stream.
/// </summary>
public class ApiMobileSseControllerTests : TestBase
{
    [Fact]
    public async Task Stream_SetsHeaders()
    {
        var sse = new Mock<SseService> { CallBase = true };
        var controller = new MobileSseController(CreateDbContext(), NullLogger<MobileSseController>.Instance, sse.Object);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        using var cts = new CancellationTokenSource(10);

        await controller.Stream("token", cts.Token);

        Assert.Equal("text/event-stream", ctx.Response.Headers["Content-Type"].ToString());
    }
}
