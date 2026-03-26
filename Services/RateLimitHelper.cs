using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Wayfarer.Services;

/// <summary>
/// Shared rate limiting utility using a sliding-window counter approximation with 1-minute windows.
/// Prevents boundary-batching attacks where a burst at the end of one window plus the start of the
/// next could double the effective limit. Thread-safe: uses atomic operations to minimize race conditions.
/// Note: during window rotation, the weighted count reads _windowStartTicks, _expirationTicks,
/// and _prevCount non-atomically, so during rotation the weight can be skewed by up to the full
/// prevCount (not just ~0.5) in the worst case — a concurrent reader may see prevWeight = 0 if
/// _windowStartTicks has not yet been updated. This is transient (lasting only the rotation instant)
/// and acceptable for rate limiting.
/// Used by <see cref="Wayfarer.Areas.Public.Controllers.TripViewerController"/> and
/// <see cref="Wayfarer.Areas.Public.Controllers.TilesController"/>.
/// </summary>
public static class RateLimitHelper
{
    /// <summary>
    /// The duration of one rate-limit window in ticks (1 minute).
    /// </summary>
    internal static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;

    /// <summary>
    /// Hard cap on the number of entries in a rate limit cache. When the cache exceeds this
    /// limit after cleanup, the oldest entries (by expiration) are evicted to bring the count
    /// down to 80% of the hard cap. Prevents unbounded memory growth from sustained low-rate
    /// attacks from many unique IPs that keep entries alive across window rotations.
    /// </summary>
    private const int HardCap = 50_000;

    /// <summary>
    /// Tracks the request count and window expiration for rate limiting using a sliding-window
    /// counter approximation. Maintains the previous window's count so that requests near a
    /// boundary are weighted, preventing the boundary-batching exploit where an attacker sends
    /// the full limit at :59s and again at :00s to achieve 2× the intended rate.
    /// Uses atomic operations (Interlocked) for thread safety. The weighted count reads multiple
    /// fields non-atomically, so it may jitter by up to the full prevCount during the rotation
    /// instant — transient and acceptable for rate limiting.
    /// </summary>
    public sealed class RateLimitEntry
    {
        private int _count;
        private int _prevCount;
        private long _expirationTicks;
        private long _windowStartTicks;

        /// <summary>
        /// Initializes a new rate limit entry with the given expiration.
        /// </summary>
        /// <param name="expirationTicks">The tick count at which this entry's window expires.</param>
        public RateLimitEntry(long expirationTicks)
        {
            _count = 0;
            _prevCount = 0;
            _expirationTicks = expirationTicks;
            _windowStartTicks = expirationTicks - WindowTicks;
        }

        /// <summary>
        /// The current window expiration ticks. Used for hard-cap eviction ordering
        /// (oldest entries are evicted first when the cache exceeds <see cref="HardCap"/>).
        /// </summary>
        public long ExpirationTicks => Interlocked.Read(ref _expirationTicks);

        /// <summary>
        /// Atomically increments the counter and returns the weighted sliding-window count.
        /// If the window has expired, rotates: atomically captures and zeroes the current count,
        /// moves the captured value to previous, and updates expiration using compare-and-swap.
        /// The returned count is: prevCount * (1 - elapsed/windowSize) + currentCount,
        /// which smoothly decays the previous window's contribution over the new window.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <param name="newExpirationTicks">The new expiration tick count if a reset occurs.</param>
        /// <returns>The weighted sliding-window request count.</returns>
        public int IncrementAndGet(long currentTicks, long newExpirationTicks)
        {
            var currentExpiration = Interlocked.Read(ref _expirationTicks);
            if (currentTicks > currentExpiration)
            {
                if (Interlocked.CompareExchange(ref _expirationTicks, newExpirationTicks, currentExpiration) == currentExpiration)
                {
                    // Won the CAS — rotate window: atomically capture and zero current count,
                    // then move captured value to previous. This eliminates the gap where
                    // concurrent increments could be lost between read and reset.
                    var captured = Interlocked.Exchange(ref _count, 0);
                    Interlocked.Exchange(ref _prevCount, captured);
                    Interlocked.Exchange(ref _windowStartTicks, currentExpiration);
                }
            }

            var currentCount = Interlocked.Increment(ref _count);

            // Compute sliding-window weighted count.
            // elapsed = how far into the current window we are (0.0 to 1.0).
            // weight = fraction of previous window still relevant (1.0 at start, 0.0 at end).
            var windowStart = Interlocked.Read(ref _windowStartTicks);
            var windowEnd = Interlocked.Read(ref _expirationTicks);
            var windowSize = windowEnd - windowStart;
            if (windowSize <= 0) windowSize = WindowTicks;
            var elapsed = currentTicks - windowStart;
            if (elapsed < 0) elapsed = 0;
            if (elapsed > windowSize) elapsed = windowSize;

            var prevWeight = 1.0 - ((double)elapsed / windowSize);
            var prev = Volatile.Read(ref _prevCount);
            var weighted = (int)(prev * prevWeight) + currentCount;

            return weighted;
        }

        /// <summary>
        /// Returns the current weighted sliding-window count WITHOUT incrementing.
        /// Used for speculative checks (e.g., per-IP outbound budget) where the
        /// increment should be deferred until the request actually proceeds upstream.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <returns>The weighted sliding-window request count (read-only).</returns>
        public int PeekCount(long currentTicks)
        {
            var currentExpiration = Interlocked.Read(ref _expirationTicks);
            // Don't rotate the window — this is a read-only peek.
            // If the window has expired, the count will be stale (0 + prevWeight * prev),
            // which is conservative: it underestimates if a rotation is pending,
            // so the caller may allow a borderline request that IncrementAndGet would block.
            // Acceptable for the two-phase pattern where the actual increment follows shortly.

            var currentCount = Volatile.Read(ref _count);

            var windowStart = Interlocked.Read(ref _windowStartTicks);
            var windowEnd = Interlocked.Read(ref _expirationTicks);
            var windowSize = windowEnd - windowStart;
            if (windowSize <= 0) windowSize = WindowTicks;
            var elapsed = currentTicks - windowStart;
            if (elapsed < 0) elapsed = 0;
            if (elapsed > windowSize) elapsed = windowSize;

            var prevWeight = 1.0 - ((double)elapsed / windowSize);
            var prev = Volatile.Read(ref _prevCount);
            return (int)(prev * prevWeight) + currentCount;
        }

        /// <summary>
        /// Returns true if this entry's window has expired.
        /// Uses a 2-window horizon: an entry is considered expired only after 2 full windows
        /// have passed, since the sliding-window algorithm needs the previous window's count.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <returns>True if expired, false otherwise.</returns>
        public bool IsExpired(long currentTicks) => currentTicks > Interlocked.Read(ref _expirationTicks) + WindowTicks;
    }

    /// <summary>
    /// Checks if the given client key has exceeded the rate limit and atomically increments the counter.
    /// Uses a sliding-window counter approximation with 1-minute windows to prevent boundary batching.
    /// </summary>
    /// <param name="cache">The concurrent dictionary tracking rate limit entries per IP.</param>
    /// <param name="clientIp">The client IP address to check.</param>
    /// <param name="maxRequestsPerMinute">Maximum allowed requests per minute.</param>
    /// <param name="maxTrackedIps">Maximum number of keys to track before cleanup triggers (default 10,000).</param>
    /// <returns>True if rate limit is exceeded, false otherwise.</returns>
    /// <summary>
    /// Coalesces concurrent cleanup runs per cache instance. Only one thread runs cleanup
    /// for a given cache at a time; others skip and proceed with rate limiting.
    /// Keyed by cache instance reference so separate caches (anonymous, authenticated, image proxy)
    /// can be cleaned independently without blocking each other.
    /// </summary>
    private static readonly ConcurrentDictionary<object, byte> _cleanupFlags = new();

    public static bool IsRateLimitExceeded(
        ConcurrentDictionary<string, RateLimitEntry> cache,
        string clientIp,
        int maxRequestsPerMinute,
        int maxTrackedIps = 10000)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var expirationTicks = currentTicks + WindowTicks;

        if (cache.Count > maxTrackedIps)
        {
            // Coalesce: only one thread runs cleanup per cache at a time.
            // TryAdd returns false if another thread is already cleaning this specific cache.
            if (_cleanupFlags.TryAdd(cache, 0))
            {
                try
                {
                    CleanupExpiredEntries(cache, currentTicks);

                    // Hard cap: if the cache still exceeds the limit after removing expired entries
                    // (e.g., sustained low-rate attack from many unique IPs), evict the oldest
                    // entries by expiration to bring the count down to 80% of the hard cap.
                    if (cache.Count > HardCap)
                    {
                        EvictOldestEntries(cache);
                    }
                }
                finally
                {
                    _cleanupFlags.TryRemove(cache, out _);
                }
            }
        }

        var entry = cache.GetOrAdd(clientIp, _ => new RateLimitEntry(expirationTicks));
        var count = entry.IncrementAndGet(currentTicks, expirationTicks);

        return count > maxRequestsPerMinute;
    }

    /// <summary>
    /// Checks if the given client key would exceed the rate limit WITHOUT incrementing the counter.
    /// Used for speculative fast-fail checks (e.g., per-IP outbound budget) where the actual
    /// increment is deferred until the request succeeds. This prevents budget-exhausted requests
    /// from inflating the per-IP counter, which would cause cascading 503 rejections on retries.
    /// </summary>
    /// <param name="cache">The concurrent dictionary tracking rate limit entries per key.</param>
    /// <param name="clientKey">The client key (IP address, user ID, etc.).</param>
    /// <param name="maxRequestsPerMinute">Maximum allowed requests per minute.</param>
    /// <returns>True if the rate limit would be exceeded, false otherwise.</returns>
    public static bool WouldExceedRateLimit(
        ConcurrentDictionary<string, RateLimitEntry> cache,
        string clientKey,
        int maxRequestsPerMinute)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var expirationTicks = currentTicks + WindowTicks;

        var entry = cache.GetOrAdd(clientKey, _ => new RateLimitEntry(expirationTicks));
        var count = entry.PeekCount(currentTicks);
        return count >= maxRequestsPerMinute;
    }

    /// <summary>
    /// Records a successful request against the rate limit counter. Call this AFTER the request
    /// has actually been fulfilled (e.g., after acquiring the global outbound budget token).
    /// Pairs with <see cref="WouldExceedRateLimit"/> for two-phase rate limiting where the
    /// check and increment are separated to avoid counting rejected requests.
    /// </summary>
    /// <param name="cache">The concurrent dictionary tracking rate limit entries per key.</param>
    /// <param name="clientKey">The client key (IP address, user ID, etc.).</param>
    public static void RecordRateLimitHit(
        ConcurrentDictionary<string, RateLimitEntry> cache,
        string clientKey)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var expirationTicks = currentTicks + WindowTicks;

        var entry = cache.GetOrAdd(clientKey, _ => new RateLimitEntry(expirationTicks));
        entry.IncrementAndGet(currentTicks, expirationTicks);
    }

    /// <summary>
    /// Removes expired entries from the rate limit cache to prevent memory growth.
    /// Called periodically when cache exceeds size threshold.
    /// </summary>
    /// <param name="cache">The concurrent dictionary to clean up.</param>
    /// <param name="currentTicks">The current tick count for expiration comparison.</param>
    public static void CleanupExpiredEntries(ConcurrentDictionary<string, RateLimitEntry> cache, long currentTicks)
    {
        foreach (var kvp in cache)
        {
            if (kvp.Value.IsExpired(currentTicks))
            {
                cache.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Evicts the oldest entries (by window expiration) from the cache to bring the count
    /// down to 80% of <see cref="HardCap"/>. Prevents unbounded memory growth from sustained
    /// low-rate attacks from many unique IPs that keep entries alive across window rotations.
    /// </summary>
    /// <param name="cache">The concurrent dictionary to evict from.</param>
    private static void EvictOldestEntries(ConcurrentDictionary<string, RateLimitEntry> cache)
    {
        var targetCount = (int)(HardCap * 0.8);
        var toEvictCount = cache.Count - targetCount;
        if (toEvictCount <= 0) return;

        var keysToEvict = cache
            .OrderBy(kvp => kvp.Value.ExpirationTicks)
            .Take(toEvictCount)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToEvict)
        {
            cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Gets the client IP address from an HTTP context, respecting X-Forwarded-For header
    /// only when the direct connection is from a trusted proxy (localhost or private IP).
    /// Normalizes IPv4-mapped IPv6 addresses to their IPv4 form to prevent aliasing
    /// (e.g., "::ffff:192.168.1.1" and "192.168.1.1" map to the same bucket key).
    /// This prevents spoofing attacks.
    /// </summary>
    /// <param name="context">The HTTP context to extract the IP from.</param>
    /// <returns>The client IP address string.</returns>
    public static string GetClientIpAddress(HttpContext context)
    {
        var directIp = context.Connection.RemoteIpAddress;
        var directIpString = directIp?.ToString() ?? "unknown";

        // Only trust X-Forwarded-For if the direct connection is from a trusted proxy
        if (directIp != null && IsPrivateOrLoopback(directIp))
        {
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                var clientIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
                // Validate as a well-formed IP and normalize to canonical form to prevent
                // IPv4/IPv6 aliasing from creating separate rate-limit buckets for the same client.
                if (!string.IsNullOrEmpty(clientIp) && IPAddress.TryParse(clientIp, out var parsed))
                {
                    return parsed.IsIPv4MappedToIPv6
                        ? parsed.MapToIPv4().ToString()
                        : parsed.ToString();
                }
            }
        }

        // Normalize direct IP the same way as forwarded IPs to prevent IPv4/IPv6 aliasing
        // (e.g., "::ffff:192.168.1.1" and "192.168.1.1" map to the same rate-limit bucket).
        if (directIp != null && directIp.IsIPv4MappedToIPv6)
            return directIp.MapToIPv4().ToString();

        return directIpString;
    }

    /// <summary>
    /// Returns true if the IP address is loopback, private (RFC 1918), link-local,
    /// IPv6 unique-local (fc00::/7), or an IPv4-mapped IPv6 address that maps to a private range.
    /// </summary>
    /// <param name="ip">The IP address to check.</param>
    /// <returns>True if the address is private or loopback.</returns>
    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.IsIPv4MappedToIPv6)
            return IsPrivateOrLoopback(ip.MapToIPv4());

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 0) return true;
        }

        if (bytes.Length == 16)
        {
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;
            if (bytes[0] == 0xfc || bytes[0] == 0xfd) return true;
        }

        return false;
    }
}
