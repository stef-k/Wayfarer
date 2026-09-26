using System.Security.Claims;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>Atomically reserves nonqueued process and identity budgets before SSE starts.</summary>
public sealed class SseAdmission(SseOptions options)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private int _active;

    internal int ActiveCount { get { lock (_gate) return _active; } }
    internal int IdentityCount { get { lock (_gate) return _identities.Count; } }

    /// <summary>Uses the controller-resolved mobile identity or authenticated claims; never parses forwarding headers.</summary>
    public IDisposable? TryAcquire(HttpContext context, string? resolvedUserId = null)
    {
        var userId = resolvedUserId ?? (context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null);
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        var key = string.IsNullOrEmpty(userId) ? "ip:" + (ip?.ToString() ?? "missing") : "user:" + userId;
        var limit = string.IsNullOrEmpty(userId) ? options.MaxConnectionsPerAnonymousIp : options.MaxConnectionsPerUser;
        lock (_gate)
        {
            _identities.TryGetValue(key, out var count);
            if (_active >= options.MaxConnections || count >= limit)
            {
                context.Response.StatusCode = _active >= options.MaxConnections ? 503 : 429;
                context.Response.Headers.RetryAfter = "5";
                return null;
            }
            _active++;
            _identities[key] = count + 1;
            return new Permit(this, key);
        }
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            _active--;
            if (--_identities[key] == 0) _identities.Remove(key);
        }
    }

    /// <summary>One idempotent reservation, including setup exceptions and competing shutdown paths.</summary>
    private sealed class Permit(SseAdmission owner, string key) : IDisposable
    {
        private SseAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key);
    }
}
