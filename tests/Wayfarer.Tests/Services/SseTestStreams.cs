using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Wayfarer.Tests.Services;

/// <summary>Gates a real response write or flush; honors the production cancellation token.</summary>
internal sealed class SseGateStream(bool flush = false, bool fail = false) : MemoryStream
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls;
    internal CancellationToken ObservedToken { get; private set; }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        if (!flush) await GateAsync(token);
        await base.WriteAsync(buffer, offset, count, token);
    }

    public override async Task FlushAsync(CancellationToken token)
    {
        if (flush) await GateAsync(token);
        await base.FlushAsync(token);
    }

    private async Task GateAsync(CancellationToken token)
    {
        ObservedToken = token;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(token);
        if (fail) throw new IOException("Controlled response failure");
    }

    internal HttpResponse Response()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = this;
        return context.Response;
    }
}

/// <summary>Controls the subscription's cleanup continuation without scheduling sleeps.</summary>
internal sealed class SsePumpContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    internal TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override void Post(SendOrPostCallback callback, object? state)
    {
        _queue.Enqueue((callback, state));
        Posted.TrySetResult();
    }
    internal void Drain()
    {
        while (_queue.TryDequeue(out var item)) item.Callback(item.State);
    }
}
