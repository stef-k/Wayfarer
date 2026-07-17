using System.Collections.Concurrent;

namespace Wayfarer.Services;

/// <summary>
/// Serializes a public timeline's settings save with its public SSE eligibility check and write.
/// </summary>
public static class PublicTimelineDeliveryLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Acquires the lease for one public timeline owner.
    /// </summary>
    /// <param name="username">The public timeline owner whose delivery is synchronized.</param>
    /// <param name="cancellationToken">Cancels the wait before the lease is acquired.</param>
    /// <returns>A lease that releases the owner's delivery lock when disposed.</returns>
    public static async Task<IAsyncDisposable> AcquireAsync(string username, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(username, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new DeliveryLease(gate);
    }

    private sealed class DeliveryLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;

        public DeliveryLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
