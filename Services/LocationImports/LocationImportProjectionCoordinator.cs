namespace Wayfarer.Services.LocationImports;

/// <summary>
/// Serializes one import's Quartz projection mutations inside the supported single
/// application-owned active-scheduler topology. Relational authority remains the
/// worker fence; this coordinator is deliberately not a distributed host lock.
/// </summary>
public sealed class LocationImportProjectionCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<int, Entry> entries = [];

    /// <summary>Shared fallback for explicitly constructed lifecycle collaborators.</summary>
    internal static LocationImportProjectionCoordinator Shared { get; } = new();

    /// <summary>Acquires cancellation-aware projection ownership for one exact import.</summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(int importId, CancellationToken token = default)
    {
        Entry entry;
        lock (sync)
        {
            if (!entries.TryGetValue(importId, out entry!)) entries.Add(importId, entry = new());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token); }
        catch
        {
            ReleaseReference(importId, entry, releaseGate: false);
            throw;
        }
        return new Lease(this, importId, entry);
    }

    private void ReleaseReference(int importId, Entry entry, bool releaseGate)
    {
        if (releaseGate) entry.Gate.Release();
        lock (sync)
        {
            entry.References--;
            if (entry.References == 0 && entries.Remove(importId, out var removed)) removed.Gate.Dispose();
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int References { get; set; }
    }

    private sealed class Lease(LocationImportProjectionCoordinator owner, int importId, Entry entry)
        : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.ReleaseReference(importId, entry, true);
            return ValueTask.CompletedTask;
        }
    }
}
