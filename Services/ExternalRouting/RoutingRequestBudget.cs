using System.Collections.Concurrent;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Enforces fixed user/global and bounded provider request and concurrency budgets.</summary>
public sealed class RoutingRequestBudget
{
    private const int UserGenerationsPerMinute = 5;
    private const int GlobalConcurrency = 8;
    private readonly SemaphoreSlim _global = new(GlobalConcurrency, GlobalConcurrency);
    private readonly ConcurrentDictionary<(Guid ProviderId, int Limit), SemaphoreSlim> _providerConcurrency = new();
    private readonly ConcurrentDictionary<string, SlidingWindow> _userWindows = new();
    private readonly ConcurrentDictionary<Guid, SlidingWindow> _providerWindows = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes budgets with an injectable clock for deterministic tests.</summary>
    public RoutingRequestBudget(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Admits one user generation and acquires provider/global concurrency.</summary>
    public async Task<RoutingBudgetLease?> AcquireAsync(
        string userId, Guid providerId, int providerRequestsPerMinute, int providerMaxConcurrency, CancellationToken cancellationToken)
    {
        if (!_userWindows.GetOrAdd(userId, _ => new SlidingWindow()).TryTake(UserGenerationsPerMinute, _timeProvider.GetUtcNow()))
            return null;
        if (!await _global.WaitAsync(0, cancellationToken)) return null;
        var providerGate = _providerConcurrency.GetOrAdd((providerId, providerMaxConcurrency),
            key => new SemaphoreSlim(key.Limit, key.Limit));
        if (!await providerGate.WaitAsync(0, cancellationToken))
        {
            _global.Release();
            return null;
        }
        return new RoutingBudgetLease(_global, providerGate,
            () => _providerWindows.GetOrAdd(providerId, _ => new SlidingWindow())
                .TryTake(providerRequestsPerMinute, _timeProvider.GetUtcNow()));
    }

    private sealed class SlidingWindow
    {
        private readonly Queue<DateTimeOffset> _entries = new();

        public bool TryTake(int limit, DateTimeOffset now)
        {
            lock (_entries)
            {
                while (_entries.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1)) _entries.Dequeue();
                if (_entries.Count >= limit) return false;
                _entries.Enqueue(now);
                return true;
            }
        }
    }
}

/// <summary>Holds concurrency and admits each initial/retry provider request against one shared budget.</summary>
public sealed class RoutingBudgetLease : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly SemaphoreSlim _provider;
    private readonly Func<bool> _admitAttempt;
    private int _disposed;

    internal RoutingBudgetLease(SemaphoreSlim global, SemaphoreSlim provider, Func<bool> admitAttempt)
        => (_global, _provider, _admitAttempt) = (global, provider, admitAttempt);

    /// <summary>Charges one actual provider attempt, including the one permitted retry.</summary>
    public bool TryAdmitProviderAttempt() => _admitAttempt();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _provider.Release();
        _global.Release();
    }
}
