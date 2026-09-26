using System.Collections.Concurrent;
using System.Text;
using Wayfarer.Models.Options;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Owns bounded best-effort SSE transport, admission and race-safe channel lifetime.</summary>
public class SseService
{
    /// <summary>Exact reload hint for authenticated invitation state.</summary>
    public const string InvitationStateHint = "{\"type\":\"invitation-state\"}";
    /// <summary>Exact reload hint for authenticated membership state.</summary>
    public const string MembershipStateHint = "{\"type\":\"membership-state\"}";
    private static readonly byte[] HeartbeatPayload = Encoding.UTF8.GetBytes(":\n\n");
    private readonly ConcurrentDictionary<string, ChannelState> _channels = new();
    private readonly SseOptions _options;
    private readonly SseAdmission _admission;
    internal int ActiveConnectionCount => _admission.ActiveCount;
    internal int ChannelCount => _channels.Count;

    /// <summary>Constructs the transport with validated application policy.</summary>
    public SseService(SseOptions options, SseAdmission admission)
    {
        options.Validate();
        _options = options;
        _admission = admission;
    }

    /// <summary>Uses production defaults for standalone callers.</summary>
    public SseService() : this(new SseOptions()) { }

    /// <summary>Uses an isolated admission owner for standalone configurations.</summary>
    public SseService(SseOptions options) : this(options, new SseAdmission(options)) { }

    /// <summary>Admits after controller authorization, then joins all owned work before releasing admission.</summary>
    public async Task SubscribeAsync(string channel, HttpResponse response, CancellationToken token,
        bool enableHeartbeat = false, TimeSpan? heartbeatInterval = null,
        Func<CancellationToken, Task<IAsyncDisposable?>>? deliveryLease = null,
        Func<string, bool>? deliveryFilter = null, string? resolvedUserId = null)
    {
        using var permit = _admission.TryAcquire(response.HttpContext, resolvedUserId);
        if (permit is null) return;
        using var client = new ClientConnection(response, token, _options.SendTimeout, deliveryLease, deliveryFilter);
        ChannelState? state = null;
        Task heartbeat = Task.CompletedTask;
        try
        {
            response.Headers.Append("Content-Type", "text/event-stream");
            response.Headers.Append("Cache-Control", "no-cache");
            state = Attach(channel, client);
            if (enableHeartbeat)
                heartbeat = client.HeartbeatAsync(heartbeatInterval ?? TimeSpan.FromSeconds(20));
            await client.Completion;
        }
        finally
        {
            client.Terminate();
            if (state is not null) Detach(channel, state, client);
            await heartbeat;
            await client.Drained;
        }
    }

    private ChannelState Attach(string channel, ClientConnection client)
    {
        while (true)
        {
            var state = _channels.GetOrAdd(channel, _ => new ChannelState());
            lock (state)
            {
                // An old candidate may have been retired while this subscriber waited for the monitor.
                if (!_channels.TryGetValue(channel, out var current) || !ReferenceEquals(current, state)) continue;
                state.Clients.Add(client);
                return state;
            }
        }
    }

    private void Detach(string channel, ChannelState state, ClientConnection client)
    {
        lock (state)
        {
            state.Clients.Remove(client);
            if (state.Clients.Count == 0)
                _channels.TryRemove(new KeyValuePair<string, ChannelState>(channel, state));
        }
    }

    /// <summary>Runs a bounded worker set under one horizon; unattempted recipients are not evicted.</summary>
    public virtual async Task BroadcastAsync(string channel, string data)
    {
        if (!_channels.TryGetValue(channel, out var state)) return;
        ClientConnection[] snapshot;
        lock (state) snapshot = state.Clients.ToArray();
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        using var horizon = new CancellationTokenSource(_options.BroadcastTimeout);
        var next = -1;
        async Task WorkerAsync()
        {
            while (!horizon.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= snapshot.Length) return;
                var client = snapshot[index];
                try
                {
                    if (client.Accepts(data)) await client.SendAsync(bytes, true, horizon.Token);
                }
                catch
                {
                    // A faulty filter is a connection failure, not a failed durable mutation.
                    client.Terminate();
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(snapshot.Length, _options.FanoutConcurrency))
            .Select(_ => WorkerAsync()));
    }

    /// <summary>Publishes a content-free reload hint to the affected user's server-owned channel.</summary>
    public Task BroadcastGroupNotificationAsync(string userId, string hint) =>
        BroadcastAsync($"group-notifications-{userId}", hint);

    /// <summary>Publishes independent best-effort group and optional private revocation hints.</summary>
    public async Task BroadcastInvitationRevocationAsync(Guid groupId, string? inviteeUserId, string groupEvent)
    {
        try
        {
            await BroadcastAsync($"group-{groupId}", groupEvent);
        }
        catch
        {
            // Durable revocation remains authoritative when presentation transport fails.
        }

        if (string.IsNullOrEmpty(inviteeUserId)) return;
        try
        {
            await BroadcastGroupNotificationAsync(inviteeUserId, InvitationStateHint);
        }
        catch
        {
            // Attempted independently so one transport cannot suppress the other.
        }
    }

    /// <summary>The monitor and collection share the dictionary entry's exact lifetime identity.</summary>
    private sealed class ChannelState
    {
        public List<ClientConnection> Clients { get; } = [];
    }

    /// <summary>Serializes two bounded send owners and joins them before disposing cancellation or semaphore state.</summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly HttpResponse _response;
        private readonly TimeSpan _timeout;
        private readonly Func<CancellationToken, Task<IAsyncDisposable?>>? _lease;
        private readonly Func<string, bool>? _filter;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationTokenRegistration _requestRegistration;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _gate = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _terminated;
        private int _pending;

        public ClientConnection(HttpResponse response, CancellationToken request, TimeSpan timeout,
            Func<CancellationToken, Task<IAsyncDisposable?>>? lease, Func<string, bool>? filter)
        {
            _response = response;
            _timeout = timeout;
            _lease = lease;
            _filter = filter;
            _requestRegistration = request.Register(Terminate);
        }

        public Task Completion => _completion.Task;
        public Task Drained => _drained.Task;
        public bool Accepts(string data) => _filter?.Invoke(data) ?? true;

        public void Terminate()
        {
            lock (_gate)
            {
                if (_terminated) return;
                _terminated = true;
                _lifetime.Cancel();
                _completion.TrySetResult();
                if (_pending == 0) _drained.TrySetResult();
            }
        }

        public async Task SendAsync(byte[] payload, bool protectedEvent, CancellationToken broadcast = default)
        {
            lock (_gate)
            {
                if (_terminated) return;
                if (_pending == 2)
                {
                    Terminate();
                    return;
                }
                _pending++;
            }
            var acquired = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, broadcast);
                deadline.CancelAfter(_timeout);
                await _sendLock.WaitAsync(deadline.Token);
                acquired = true;
                await using var lease = protectedEvent && _lease is not null ? await _lease(deadline.Token) : null;
                if (protectedEvent && _lease is not null && lease is null)
                {
                    Terminate();
                    return;
                }
                deadline.Token.ThrowIfCancellationRequested();
                await _response.Body.WriteAsync(payload, 0, payload.Length, deadline.Token);
                await _response.Body.FlushAsync(deadline.Token);
            }
            catch
            {
                // Cancellation, lease errors and write/flush failures all fail closed for this client only.
                Terminate();
            }
            finally
            {
                if (acquired) _sendLock.Release();
                lock (_gate)
                {
                    _pending--;
                    if (_terminated && _pending == 0) _drained.TrySetResult();
                }
            }
        }

        public async Task HeartbeatAsync(TimeSpan interval)
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(_lifetime.Token))
                    await SendAsync(HeartbeatPayload, false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { Terminate(); }
        }

        public void Dispose()
        {
            // Subscribe joins heartbeat and all admitted sends before reaching this point.
            _requestRegistration.Dispose();
            _sendLock.Dispose();
            _lifetime.Dispose();
        }
    }
}
