using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Exercises real redirect/pooling behavior with controlled resolution and pinned socket endpoints.</summary>
public sealed class ImageOriginTransportTests
{
    /// <summary>Cross-host and same-host hops validate each new connection and reuse a safe existing socket.</summary>
    [Fact]
    public async Task Redirects_UseValidatedDirectConnections_AndIgnoreConfiguredProxy()
    {
        await using var server = new OriginServer(path => path switch
        {
            "/start" => "http://second.example/same",
            "/same" => "/last",
            _ => null
        });
        var resolved = new ConcurrentQueue<string>();
        using var handler = ImageOriginTransport.CreateHandler((host, _) =>
        {
            resolved.Enqueue(host);
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        }, server.ConnectAsync);
        handler.Proxy = new WebProxy("http://127.0.0.1:1");
        Assert.False(handler.UseProxy);
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://first.example/start");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "first.example", "second.example" }, resolved.ToArray());
        Assert.Equal(2, server.Connections);
        Assert.Equal(3, server.Requests);
    }

    /// <summary>A private literal, private DNS or mixed answer is rejected before any destination socket opens.</summary>
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("private.example", false)]
    [InlineData("mixed.example", true)]
    public async Task Redirects_RejectPrivateDestinationBeforeConnecting(string target, bool mixed)
    {
        await using var server = new OriginServer(_ => $"http://{target}/denied");
        using var handler = ImageOriginTransport.CreateHandler((host, _) => Task.FromResult(
            host == "public.example" ? new[] { IPAddress.Parse("93.184.216.34") } :
            mixed ? new[] { IPAddress.Parse("93.184.216.35"), IPAddress.Loopback } : new[] { IPAddress.Loopback }),
            server.ConnectAsync);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://public.example/start"));
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, server.Requests);
    }

    /// <summary>The retained runtime redirect loop has a finite 50-hop limit.</summary>
    [Fact]
    public async Task Redirects_StopAtFiniteLimit()
    {
        await using var server = new OriginServer(_ => "/again");
        using var handler = ImageOriginTransport.CreateHandler(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), server.ConnectAsync);
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://public.example/start");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(51, server.Requests);
        Assert.Equal(1, server.Connections);
    }

    /// <summary>HTTP/1.1 fixture maps validated public IPs to an owned loopback listener, retaining real sockets.</summary>
    private sealed class OriginServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Func<string, string?> _redirect;
        private readonly Task _accept;
        private int _connectionCount;
        private int _requests;
        public int Connections => _connectionCount;
        public int Requests => _requests;

        public OriginServer(Func<string, string?> redirect)
        {
            _redirect = redirect;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
        {
            Assert.False(Wayfarer.Services.RateLimitHelper.IsPrivateOrLoopback(address));
            Interlocked.Increment(ref _connectionCount);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(_listener.LocalEndpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                try
                {
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        var path = line.Split(' ')[1];
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                        Interlocked.Increment(ref _requests);
                        var location = _redirect(path);
                        var response = location == null ? "HTTP/1.1 200 OK\r\n" :
                            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "Content-Length: 0\r\n\r\n"), _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept;
            _listener.Stop();
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
