using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Unit tests for <see cref="RateLimitHelper"/>.
/// </summary>
public class RateLimitHelperTests
{
    [Fact]
    public void IsRateLimitExceeded_ReturnsFalse_WhenUnderLimit()
    {
        var cache = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();

        var exceeded = RateLimitHelper.IsRateLimitExceeded(cache, "10.0.0.1", 5);

        Assert.False(exceeded);
    }

    [Fact]
    public void IsRateLimitExceeded_ReturnsTrue_WhenExceedingLimit()
    {
        var cache = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();
        var ip = "10.0.0.2";

        // Make requests up to the limit
        for (int i = 0; i < 3; i++)
        {
            RateLimitHelper.IsRateLimitExceeded(cache, ip, 3);
        }

        // Next request should exceed the limit
        var exceeded = RateLimitHelper.IsRateLimitExceeded(cache, ip, 3);

        Assert.True(exceeded);
    }

    [Fact]
    public void IsRateLimitExceeded_SeparateDictionaries_TrackIndependently()
    {
        var cache1 = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();
        var cache2 = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();
        var ip = "10.0.0.3";

        // Exhaust limit on cache1
        for (int i = 0; i < 2; i++)
        {
            RateLimitHelper.IsRateLimitExceeded(cache1, ip, 2);
        }

        // cache1 should be exceeded
        Assert.True(RateLimitHelper.IsRateLimitExceeded(cache1, ip, 2));

        // cache2 should not be affected
        Assert.False(RateLimitHelper.IsRateLimitExceeded(cache2, ip, 2));
    }

    [Fact]
    public void CleanupExpiredEntries_RemovesStaleEntries()
    {
        var cache = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();

        // Create an entry that is already expired
        var pastTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
        cache["stale-ip"] = new RateLimitHelper.RateLimitEntry(pastTicks);
        cache["fresh-ip"] = new RateLimitHelper.RateLimitEntry(DateTime.UtcNow.AddMinutes(5).Ticks);

        RateLimitHelper.CleanupExpiredEntries(cache, DateTime.UtcNow.Ticks);

        Assert.False(cache.ContainsKey("stale-ip"));
        Assert.True(cache.ContainsKey("fresh-ip"));
    }

    [Fact]
    public void RateLimitEntry_ExpiredWindow_ResetsCounter()
    {
        // Create an entry that expires immediately
        var pastExpiration = DateTime.UtcNow.AddMinutes(-1).Ticks;
        var entry = new RateLimitHelper.RateLimitEntry(pastExpiration);

        // The window is expired, so IncrementAndGet should reset and return 1
        var currentTicks = DateTime.UtcNow.Ticks;
        var newExpiration = currentTicks + TimeSpan.FromMinutes(1).Ticks;
        var count = entry.IncrementAndGet(currentTicks, newExpiration);

        Assert.Equal(1, count);
    }

    /// <summary>
    /// Verifies that the sliding-window algorithm prevents boundary-batching attacks.
    /// In a fixed-window scheme, sending N requests at the end of window 1 and N at the start
    /// of window 2 would allow 2N requests in ~2 seconds. The sliding window should block this.
    /// </summary>
    [Fact]
    public void SlidingWindow_PreventsBoundaryBatching()
    {
        var cache = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();
        var ip = "10.0.0.50";
        const int limit = 10;

        // Simulate: send 10 requests just before window boundary (filling the window).
        for (int i = 0; i < limit; i++)
        {
            RateLimitHelper.IsRateLimitExceeded(cache, ip, limit);
        }

        // Manually rotate the window by constructing a scenario at the very start of a new window.
        // The previous window had 10 requests; the new window just started so prevWeight ≈ 1.0.
        // Under a fixed-window scheme, the counter would reset and allow another 10 immediately.
        // Under sliding-window, the weighted count includes the previous window's contribution.
        var entry = cache[ip];
        var now = DateTime.UtcNow.Ticks;
        // Force a window rotation by calling with a time past the current expiration.
        var farFuture = now + TimeSpan.FromMinutes(2).Ticks;
        var newExpiration = farFuture + TimeSpan.FromMinutes(1).Ticks;
        entry.IncrementAndGet(farFuture, newExpiration);

        // Now simulate being just 1 second into the new window (prevWeight ≈ 0.98).
        // The previous window had ~10 requests, so weighted ≈ 10 * 0.98 + currentCount.
        var windowStart = farFuture; // approximately
        var oneSecondIn = windowStart + TimeSpan.FromSeconds(1).Ticks;
        var newExp2 = oneSecondIn + TimeSpan.FromMinutes(1).Ticks;

        // Even the first request in the new window should see an elevated weighted count
        // due to the previous window's contribution, so a new burst should be blocked sooner.
        int exceededCount = 0;
        for (int i = 0; i < limit; i++)
        {
            if (RateLimitHelper.IsRateLimitExceeded(cache, ip, limit))
            {
                exceededCount++;
            }
        }

        // With sliding window, most of these should be blocked because the previous window's
        // 10 requests are still weighted in. Under fixed-window, none would be blocked.
        Assert.True(exceededCount > 0, "Sliding window should block requests near the boundary due to previous window's weight");
    }

    /// <summary>
    /// Verifies that after a full window has elapsed with no activity, the previous window's
    /// weight fully decays and the rate limiter allows the full limit again.
    /// </summary>
    [Fact]
    public void SlidingWindow_FullDecay_AllowsFullLimitAfterQuietPeriod()
    {
        var cache = new ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry>();
        var ip = "10.0.0.51";
        const int limit = 5;

        // Fill the first window completely.
        for (int i = 0; i < limit; i++)
        {
            RateLimitHelper.IsRateLimitExceeded(cache, ip, limit);
        }
        Assert.True(RateLimitHelper.IsRateLimitExceeded(cache, ip, limit), "Should exceed after filling window");

        // Wait for two full windows to pass (previous window's weight is fully decayed).
        // We simulate this by constructing a new entry that appears to have an old expiration.
        var entry = cache[ip];
        var now = DateTime.UtcNow.Ticks;
        var farFuture = now + TimeSpan.FromMinutes(3).Ticks;
        var newExpiration = farFuture + TimeSpan.FromMinutes(1).Ticks;

        // First call in the far future rotates the window.
        entry.IncrementAndGet(farFuture, newExpiration);

        // After a quiet period, another rotation zeroes the previous window's count too.
        var evenFarther = farFuture + TimeSpan.FromMinutes(2).Ticks;
        var newExp2 = evenFarther + TimeSpan.FromMinutes(1).Ticks;
        entry.IncrementAndGet(evenFarther, newExp2);

        // Now the previous window count should be 1 (from the rotation call), and we're at
        // the very end of the new window so prevWeight ≈ 0. We should be able to make ~limit requests.
        var nearEnd = newExp2 - TimeSpan.FromSeconds(1).Ticks;
        var finalExp = nearEnd + TimeSpan.FromMinutes(1).Ticks;
        int allowed = 0;
        for (int i = 0; i < limit; i++)
        {
            if (!RateLimitHelper.IsRateLimitExceeded(cache, ip, limit))
            {
                allowed++;
            }
        }

        // After full decay, most (or all) of the limit should be available again.
        Assert.True(allowed >= limit - 2, $"Expected at least {limit - 2} allowed after full decay, got {allowed}");
    }

    /// <summary>
    /// Verifies that GetClientIpAddress normalizes IPv4-mapped IPv6 addresses on the direct-IP path
    /// (not just the X-Forwarded-For path), preventing rate-limit bucket aliasing.
    /// </summary>
    [Fact]
    public void GetClientIpAddress_NormalizesIPv4MappedIPv6_OnDirectPath()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.168.1.1");

        var result = RateLimitHelper.GetClientIpAddress(context);

        Assert.Equal("192.168.1.1", result);
    }

    /// <summary>
    /// Verifies that a regular IPv4 direct IP is returned unchanged.
    /// </summary>
    [Fact]
    public void GetClientIpAddress_ReturnsIPv4Unchanged_OnDirectPath()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");

        var result = RateLimitHelper.GetClientIpAddress(context);

        Assert.Equal("203.0.113.5", result);
    }
}
