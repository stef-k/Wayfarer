using System.Net;
using System.Net.Sockets;
using Wayfarer.Services;

namespace Wayfarer.Util;

/// <summary>Direct image-origin connections with DNS validation and IP-pinned socket creation.</summary>
internal static class ImageOriginTransport
{
    /// <summary>Retains finite automatic redirects, including the runtime's HTTPS downgrade rejection.</summary>
    internal static SocketsHttpHandler CreateHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task<Stream>>? connect = null) => new()
    {
        // A system proxy would make the callback validate the proxy rather than the origin.
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 50,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await (resolve ?? Dns.GetHostAddressesAsync)(context.DnsEndPoint.Host, ct);
            foreach (var address in addresses)
            {
                if (RateLimitHelper.IsPrivateOrLoopback(address))
                    throw new HttpRequestException("Image origin address is disallowed.");
            }

            Exception? lastException = null;
            foreach (var address in addresses)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await (connect ?? ConnectAsync)(address, context.DnsEndPoint.Port, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { lastException = ex; }
            }
            throw new HttpRequestException("Could not connect to image origin.", lastException);
        }
    };

    /// <summary>Connects to the already validated IP without a second DNS lookup.</summary>
    private static async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
