using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Wayfarer.Services;

/// <summary>
/// Shared rate limiting utility using a sliding-window counter approximation with 1-minute windows.
/// Prevents boundary-batching attacks where a burst at the end of one window plus the start of the
/// next could double the effective limit. Thread-safe: uses atomic operations to minimize race conditions.
/// Note: during window rotation, the weighted count reads _windowStartTicks, _expirationTicks,
/// and _prevCount non-atomically, so during rotation the weight can be skewed by up to
/// prevCount × ~0.5 in the worst case. This is transient (lasting only the rotation instant)
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
    /// Tracks the request count and window expiration for rate limiting using a sliding-window
    /// counter approximation. Maintains the previous window's count so that requests near a
    /// boundary are weighted, preventing the boundary-batching exploit where an attacker sends
    /// the full limit at :59s and again at :00s to achieve 2× the intended rate.
    /// Uses atomic operations (Interlocked) for thread safety. The weighted count reads multiple
    /// fields non-atomically, so it may jitter by up to prevCount × ~0.5 during window rotation —
    /// transient and acceptable for rate limiting.
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
    /// Gets the client IP address from an HTTP context, respecting X-Forwarded-For header
    /// only when the direct connection is from a trusted proxy (localhost or private IP).
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
                // Validate as a well-formed IP to prevent arbitrary strings from being used
                // as rate-limit bucket keys (e.g., a malformed header creating unique buckets).
                if (!string.IsNullOrEmpty(clientIp) && IPAddress.TryParse(clientIp, out _))
                {
                    return clientIp;
                }
            }
        }

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
