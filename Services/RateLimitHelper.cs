using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Wayfarer.Services;

/// <summary>
/// Shared rate limiting utility using a fixed-window approach with 1-minute expiration.
/// Thread-safe: uses atomic operations to prevent race conditions.
/// Used by <see cref="Wayfarer.Areas.Public.Controllers.TripViewerController"/> and
/// <see cref="Wayfarer.Areas.Public.Controllers.TilesController"/>.
/// </summary>
public static class RateLimitHelper
{
    /// <summary>
    /// Tracks the request count and window expiration for rate limiting.
    /// Uses atomic operations (Interlocked) for thread safety.
    /// </summary>
    public sealed class RateLimitEntry
    {
        private int _count;
        private long _expirationTicks;

        /// <summary>
        /// Initializes a new rate limit entry with the given expiration.
        /// </summary>
        /// <param name="expirationTicks">The tick count at which this entry's window expires.</param>
        public RateLimitEntry(long expirationTicks)
        {
            _count = 0;
            _expirationTicks = expirationTicks;
        }

        /// <summary>
        /// Atomically increments the counter and returns the new count.
        /// If the window has expired, resets the counter and updates expiration using
        /// compare-and-swap to avoid TOCTOU race conditions.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <param name="newExpirationTicks">The new expiration tick count if a reset occurs.</param>
        /// <returns>The incremented request count for the current window.</returns>
        public int IncrementAndGet(long currentTicks, long newExpirationTicks)
        {
            var currentExpiration = Interlocked.Read(ref _expirationTicks);
            if (currentTicks > currentExpiration)
            {
                if (Interlocked.CompareExchange(ref _expirationTicks, newExpirationTicks, currentExpiration) == currentExpiration)
                {
                    Interlocked.Exchange(ref _count, 0);
                }
            }

            return Interlocked.Increment(ref _count);
        }

        /// <summary>
        /// Returns true if this entry's window has expired.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <returns>True if expired, false otherwise.</returns>
        public bool IsExpired(long currentTicks) => currentTicks > Interlocked.Read(ref _expirationTicks);
    }

    /// <summary>
    /// Checks if the given IP has exceeded the rate limit and atomically increments the counter.
    /// Uses a fixed window approach with 1-minute expiration.
    /// </summary>
    /// <param name="cache">The concurrent dictionary tracking rate limit entries per IP.</param>
    /// <param name="clientIp">The client IP address to check.</param>
    /// <param name="maxRequestsPerMinute">Maximum allowed requests per minute.</param>
    /// <param name="maxTrackedIps">Maximum number of IPs to track before cleanup triggers.</param>
    /// <returns>True if rate limit is exceeded, false otherwise.</returns>
    public static bool IsRateLimitExceeded(
        ConcurrentDictionary<string, RateLimitEntry> cache,
        string clientIp,
        int maxRequestsPerMinute,
        int maxTrackedIps = 100000)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var expirationTicks = currentTicks + TimeSpan.FromMinutes(1).Ticks;

        if (cache.Count > maxTrackedIps)
        {
            CleanupExpiredEntries(cache, currentTicks);
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
                if (!string.IsNullOrEmpty(clientIp))
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
