using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SseService>(_ => new SseService());
builder.Services.AddControllers();
var app = builder.Build();
app.MapControllers();
await app.RunAsync();

/// <summary>Disposable MVC actions prove RequestAborted binding with the production transport.</summary>
[ApiController]
public sealed class ProbeController(SseService service) : ControllerBase
{
    private static readonly ConcurrentDictionary<string, bool> Cancelled = new();
    private static readonly ConcurrentDictionary<string, bool> SameToken = new();

    /// <summary>First event and the real default twenty-second heartbeat.</summary>
    [HttpGet("/sse/{id}")]
    public async Task Stream(string id, CancellationToken cancellationToken)
    {
        SameToken[id] = cancellationToken == HttpContext.RequestAborted;
        using var registration = cancellationToken.Register(() => Cancelled[id] = true);
        var subscription = service.SubscribeAsync(id, Response, cancellationToken, enableHeartbeat: true);
        await service.BroadcastAsync(id, "{\"ready\":true}");
        await subscription;
    }

    /// <summary>Independent upstream diagnostics are confined to this disposable host.</summary>
    [HttpGet("/state/{id}")]
    public object State(string id) => new { cancelled = Cancelled.ContainsKey(id),
        sameToken = SameToken.GetValueOrDefault(id),
        clients = service.ActiveConnectionCount, channels = service.ChannelCount };

    /// <summary>Representative finite-response proxy control.</summary>
    [HttpGet("/normal")]
    public object Normal() => new { normal = true };
}
