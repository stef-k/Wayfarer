using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real forwarding middleware is the only authority allowed to interpret XFF.</summary>
public class EffectiveClientAddressTests
{
    /// <summary>Residual headers and hostile peers never override the middleware-resolved address.</summary>
    [Theory]
    [InlineData("203.0.113.9", null, "203.0.113.9")]
    [InlineData("10.20.30.40", "198.51.100.7", "198.51.100.7")]
    [InlineData("203.0.113.9", "198.51.100.7", "203.0.113.9")]
    [InlineData("192.168.1.2", "198.51.100.7", "192.168.1.2")]
    [InlineData("127.0.0.1", "198.51.100.7", "127.0.0.1")]
    [InlineData("10.20.30.40", "198.51.100.7, 192.168.1.2", "192.168.1.2")]
    [InlineData("::ffff:203.0.113.9", "198.51.100.7", "203.0.113.9")]
    [InlineData(null, null, "unknown")]
    public async Task UsesPostMiddlewareAddress(string? peer, string? xff, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer == null ? null : IPAddress.Parse(peer);
        if (xff != null) context.Request.Headers["X-Forwarded-For"] = xff;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Parse("10.20.30.40"));
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
            NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
        Assert.Equal(expected, RateLimitHelper.GetClientIpAddress(context));
    }
}
