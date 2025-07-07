using System.Collections.Concurrent;
using System.Text;

namespace Wayfarer.Parsers;

/// <summary>
/// Server Send Events Service to broadcast messages to clients.
/// The service uses simple channels to subscribe to and has the form of, channel, message, cancellation token and
/// 2 methods, subscribe and broadcast.
/// </summary>
public class SseService
{
    // channel name → list of active client streams
    private readonly ConcurrentDictionary<string, List<ClientConnection>> _channels 
        = new();

    /// <summary>
    /// Lets clients to subscribe to chennels
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="response"></param>
    /// <param name="token"></param>
    public async Task SubscribeAsync(string channel, HttpResponse response, CancellationToken token)
    {
        response.Headers.Add("Content-Type", "text/event-stream");
        response.Headers.Add("Cache-Control", "no-cache");
        var client = new ClientConnection(response);

        var subscribers = _channels.GetOrAdd(channel, _ => new List<ClientConnection>());
        lock (subscribers) { subscribers.Add(client); }

        try
        {
            // hold the request open until the client disconnects
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException) { /* client went away */ }
        finally
        {
            // cleanup
            lock (subscribers) { subscribers.Remove(client); }
        }
    }

    /// <summary>
    /// Broadcasts a message to subscribed clients
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="data">The message to broadcast</param>
    public async Task BroadcastAsync(string channel, string data)
    {
        if (!_channels.TryGetValue(channel, out var subscribers)) 
            return;

        List<ClientConnection> copy;
        lock (subscribers) { copy = subscribers.ToList(); }

        var sseData = $"data: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(sseData);

        foreach (var c in copy)
        {
            try
            {
                await c.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                await c.Response.Body.FlushAsync();
            }
            catch
            {
                // remove dead client
                lock (subscribers) { subscribers.Remove(c); }
            }
        }
    }

    private class ClientConnection
    {
        public HttpResponse Response { get; }
        public ClientConnection(HttpResponse resp) => Response = resp;
    }
}
