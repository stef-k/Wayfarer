using System.Collections.Concurrent;
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
}
