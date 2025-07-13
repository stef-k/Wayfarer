using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/sse/stream")]
public class SseController : Controller
{
    private readonly SseService _sse;
    public SseController(SseService sse) => _sse = sse;

    // Matches: /sse/stream/{type}/{id}
    [HttpGet("{type}/{id}")]
    public async Task Stream(string type, string id, CancellationToken ct)
    {
        var channel = $"{type}-{id}";
        await _sse.SubscribeAsync(channel, Response, ct);
    }
}