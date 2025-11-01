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
        TimeSpan? heartbeatInterval = null)
    {
        response.Headers.Add("Content-Type", "text/event-stream");
        response.Headers.Add("Cache-Control", "no-cache");
        var client = new ClientConnection(response, HeartbeatPayload);

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
    public async Task BroadcastAsync(string channel, string data)
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
            var success = await client.SendAsync(bytes);
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
        private Timer? _heartbeatTimer;
        private bool _disposed;

        public ClientConnection(HttpResponse response, byte[] heartbeatPayload)
        {
            _response = response;
            _heartbeatPayload = heartbeatPayload;
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