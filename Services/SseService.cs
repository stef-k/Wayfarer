using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wayfarer.Parsers;

/// <summary>
/// Server Send Events Service to broadcast messages to clients.
/// </summary>
public class SseService
{
    private static readonly byte[] HeartbeatPayload = Encoding.UTF8.GetBytes(":\n\n");

    // channel name -> list of active client streams
    private readonly ConcurrentDictionary<string, List<ClientConnection>> _channels = new();

    /// <summary>
    /// Lets clients subscribe to channels.
    /// </summary>
    public async Task SubscribeAsync(
        string channel,
        HttpResponse response,
        CancellationToken token,
        bool enableHeartbeat = false,
        TimeSpan? heartbeatInterval = null,
        Func<CancellationToken, Task<IAsyncDisposable?>>? deliveryLease = null)
    {
        response.Headers.Append("Content-Type", "text/event-stream");
        response.Headers.Append("Cache-Control", "no-cache");
        var client = new ClientConnection(response, HeartbeatPayload, deliveryLease);

        var subscribers = _channels.GetOrAdd(channel, _ => new List<ClientConnection>());
        lock (subscribers)
        {
            subscribers.Add(client);
        }

        if (enableHeartbeat)
        {
            client.StartHeartbeat(heartbeatInterval ?? TimeSpan.FromSeconds(20));
        }

        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        finally
        {
            lock (subscribers)
            {
                subscribers.Remove(client);
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Broadcasts a message to subscribed clients.
    /// </summary>
    public virtual async Task BroadcastAsync(string channel, string data)
    {
        if (!_channels.TryGetValue(channel, out var subscribers))
        {
            return;
        }

        List<ClientConnection> snapshot;
        lock (subscribers)
        {
            snapshot = subscribers.ToList();
        }

        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");

        foreach (var client in snapshot)
        {
            var success = await client.SendIfEligibleAsync(bytes);
            if (!success)
            {
                lock (subscribers)
                {
                    subscribers.Remove(client);
                }

                client.Dispose();
            }
        }
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly HttpResponse _response;
        private readonly byte[] _heartbeatPayload;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Func<CancellationToken, Task<IAsyncDisposable?>>? _deliveryLease;
        private Timer? _heartbeatTimer;
        private bool _disposed;

        public ClientConnection(HttpResponse response, byte[] heartbeatPayload, Func<CancellationToken, Task<IAsyncDisposable?>>? deliveryLease)
        {
            _response = response;
            _heartbeatPayload = heartbeatPayload;
            _deliveryLease = deliveryLease;
        }

        public void StartHeartbeat(TimeSpan interval)
        {
            _heartbeatTimer = new Timer(static state =>
            {
                var connection = (ClientConnection)state!;
                _ = connection.SendHeartbeatAsync();
            }, this, interval, interval);
        }

        public async Task<bool> SendAsync(byte[] payload)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                await _sendLock.WaitAsync();
                await _response.Body.WriteAsync(payload, 0, payload.Length);
                await _response.Body.FlushAsync();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Sends an event only while its subscription remains eligible and its optional delivery lease is held.
        /// </summary>
        public async Task<bool> SendIfEligibleAsync(byte[] payload)
        {
            IAsyncDisposable? deliveryLease = _deliveryLease is null
                ? null
                : await _deliveryLease(CancellationToken.None);
            if (_deliveryLease is not null && deliveryLease is null)
            {
                return false;
            }

            try
            {
                return await SendAsync(payload);
            }
            finally
            {
                if (deliveryLease is not null)
                {
                    await deliveryLease.DisposeAsync();
                }
            }
        }

        private Task<bool> SendHeartbeatAsync() => SendAsync(_heartbeatPayload);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _heartbeatTimer?.Dispose();
            _sendLock.Dispose();
        }
    }
}
