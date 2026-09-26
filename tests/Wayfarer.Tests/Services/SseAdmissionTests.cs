using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Finite default envelope, atomic limits and exact-once reservation cleanup.</summary>
public class SseAdmissionTests
{
    /// <summary>Manager rows/tabs and mobile streams share the resolved user budget.</summary>
    [Fact]
    public void DefaultEnvelopeAdmits206Streams()
    {
        var admission = new SseAdmission(new SseOptions());
        var permits = Enumerable.Range(0, 206).Select(_ => admission.TryAcquire(Context(), "manager")).ToArray();
        Assert.All(permits, permit => Assert.NotNull(permit));
        foreach (var permit in permits) permit!.Dispose();
        Assert.Equal(0, admission.ActiveCount);
        Assert.Equal(0, admission.IdentityCount);
    }

    /// <summary>Default boundaries reject immediately with explicit pre-stream retry guidance.</summary>
    [Theory]
    [InlineData("global", 2048, 503)]
    [InlineData("user", 256, 429)]
    [InlineData("anonymous", 128, 429)]
    public void ExactDefaultBoundary(string kind, int capacity, int status)
    {
        var admission = new SseAdmission(new SseOptions());
        var permits = new List<IDisposable>();
        for (var i = 0; i < capacity; i++)
            permits.Add(Assert.IsAssignableFrom<IDisposable>(admission.TryAcquire(Context(),
                kind == "global" ? $"user-{i}" : kind == "user" ? "same" : null)));
        var rejected = Context();
        Assert.Null(admission.TryAcquire(rejected, kind == "global" ? "another" : kind == "user" ? "same" : null));
        Assert.Equal(status, rejected.Response.StatusCode);
        Assert.Equal("5", rejected.Response.Headers.RetryAfter);
        Assert.False(rejected.Response.Headers.ContainsKey("Content-Type"));
        foreach (var permit in permits) { permit.Dispose(); permit.Dispose(); }
        Assert.Equal(0, admission.ActiveCount);
        Assert.Equal(0, admission.IdentityCount);
    }

    /// <summary>Authenticated NAT sharing is independent; bearer-resolved and cookie identities aggregate.</summary>
    [Fact]
    public void AuthenticatedIdentityOverridesIpAndAggregatesAcrossAuthMethods()
    {
        var options = new SseOptions { MaxConnectionsPerUser = 1, MaxConnectionsPerAnonymousIp = 1 };
        var admission = new SseAdmission(options);
        var context = Context();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "one")], "cookie"));
        using var one = admission.TryAcquire(context);
        using var two = admission.TryAcquire(Context(), "two");
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Null(admission.TryAcquire(Context(), "one"));
    }

    /// <summary>Mapped IPv4 shares a bucket; missing addresses share one fallback and headers cannot override it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnonymousAddressesUseEffectiveNormalizedBucket(bool mapped)
    {
        var admission = new SseAdmission(new SseOptions { MaxConnectionsPerAnonymousIp = 1 });
        var first = Context();
        var second = Context();
        first.Connection.RemoteIpAddress = mapped ? IPAddress.Parse("192.0.2.1") : null;
        second.Connection.RemoteIpAddress = mapped ? IPAddress.Parse("::ffff:192.0.2.1") : null;
        second.Request.Headers["X-Forwarded-For"] = "203.0.113.55";
        using var permit = admission.TryAcquire(first);
        Assert.Null(admission.TryAcquire(second));
        Assert.Equal(1, admission.IdentityCount);
    }

    /// <summary>Concurrent admission never exceeds the configured cap or retains zero-count keys.</summary>
    [Fact]
    public async Task ConcurrentAdmissionsAreAtomic()
    {
        var admission = new SseAdmission(new SseOptions { MaxConnections = 8, MaxConnectionsPerUser = 8, MaxConnectionsPerAnonymousIp = 8 });
        var permits = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => admission.TryAcquire(Context(), "same"))));
        Assert.Equal(8, permits.Count(p => p is not null));
        foreach (var permit in permits) permit?.Dispose();
        Assert.Equal(0, admission.ActiveCount);
        Assert.Equal(0, admission.IdentityCount);
    }

    /// <summary>Header setup failure after reservation releases the permit; rejection never registers a channel.</summary>
    [Fact]
    public async Task SetupFailureAndRejectionDoNotLeak()
    {
        var options = new SseOptions { MaxConnectionsPerAnonymousIp = 1 };
        var admission = new SseAdmission(options);
        var service = new SseService(options, admission);
        var broken = Context();
        ((HeaderDictionary)broken.Response.Headers).IsReadOnly = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubscribeAsync("broken", broken.Response, CancellationToken.None));
        Assert.Equal(0, admission.ActiveCount);
        using var permit = admission.TryAcquire(Context());
        var rejected = Context();
        await service.SubscribeAsync("rejected", rejected.Response, CancellationToken.None);
        Assert.Equal(429, rejected.Response.StatusCode);
        Assert.Equal(0, service.ChannelCount);
    }

    /// <summary>Invalid overrides are rejected rather than disabling finite bounds.</summary>
    [Fact]
    public void InvalidOptionsFailEarly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SseService(new SseOptions { SendTimeout = Timeout.InfiniteTimeSpan }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SseService(new SseOptions { FanoutConcurrency = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SseService(new SseOptions { MaxConnectionsPerUser = 2049 }));
        Assert.Equal(TimeSpan.FromSeconds(10), new SseOptions().SendTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), new MobileSseOptions().HeartbeatInterval);
    }

    private static DefaultHttpContext Context() => new();
}
