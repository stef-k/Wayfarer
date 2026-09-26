namespace Wayfarer.Services;

/// <summary>
/// Process-local, nonwaiting admission for token lookup and location ingestion.
/// Only live work owns identity buckets; disposal atomically releases both ceilings.
/// </summary>
public sealed class ApiWorkAdmission(int globalLimit, int identityLimit)
{
    /// <summary>Shared lookup ceiling, keyed only by effective client IP.</summary>
    public static ApiWorkAdmission TokenLookups { get; } = new(32, 16);

    /// <summary>Shared ingestion ceiling across endpoints and tokens, keyed by user ID.</summary>
    public static ApiWorkAdmission LocationIngestion { get; } = new(64, 8);

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private int _active;

    /// <summary>Number of live identity buckets; zero-count buckets are never retained.</summary>
    public int IdentityCount { get { lock (_sync) return _identities.Count; } }

    /// <summary>Atomically admits work or reports global (503) or identity (429) saturation.</summary>
    public IDisposable? TryAcquire(string identity, out int status)
    {
        lock (_sync)
        {
            status = 503;
            if (_active >= globalLimit) return null;
            _identities.TryGetValue(identity, out var count);
            status = 429;
            if (count >= identityLimit) return null;
            _active++;
            _identities[identity] = count + 1;
            status = 0;
            return new Lease(this, identity);
        }
    }

    /// <summary>Releases counts together so cleanup cannot create a second live bucket.</summary>
    private void Release(string identity)
    {
        lock (_sync)
        {
            _active--;
            if (--_identities[identity] == 0) _identities.Remove(identity);
        }
    }

    /// <summary>An idempotently disposable reservation.</summary>
    private sealed class Lease(ApiWorkAdmission owner, string identity) : IDisposable
    {
        private ApiWorkAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(identity);
    }
}
