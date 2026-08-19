using System.Collections.Concurrent;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Enforces fixed user/global and bounded provider request and concurrency budgets.</summary>
public sealed class RoutingRequestBudget
{
    private const int UserGenerationsPerMinute = 5;
    private const int GlobalConcurrency = 8;
    private readonly SemaphoreSlim _global = new(GlobalConcurrency, GlobalConcurrency);
    private readonly ConcurrentDictionary<Guid, ProviderGate> _providerConcurrency = new();
    private readonly ConcurrentDictionary<string, SlidingWindow> _userWindows = new();
    private readonly ConcurrentDictionary<Guid, SlidingWindow> _providerWindows = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes budgets with an injectable clock for deterministic tests.</summary>
    public RoutingRequestBudget(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Admits one complete generation operation against the per-user window.</summary>
    public bool TryAdmitUserGeneration(string userId) =>
        _userWindows.GetOrAdd(userId, _ => new SlidingWindow()).TryTake(UserGenerationsPerMinute, _timeProvider.GetUtcNow());

    /// <summary>Acquires fail-fast global then durable provider concurrency for one actual attempt.</summary>
    public async Task<RoutingConcurrencyLease?> AcquireAttemptConcurrencyAsync(
        Guid providerId, int providerMaxConcurrency, CancellationToken cancellationToken)
    {
        if (!await _global.WaitAsync(0, cancellationToken)) return null;
        var providerGate = _providerConcurrency.GetOrAdd(providerId, _ => new ProviderGate());
        if (!providerGate.TryAcquire(providerMaxConcurrency)) { _global.Release(); return null; }
        return new RoutingConcurrencyLease(_global, providerGate.Release);
    }

    /// <summary>Charges exactly one actual provider attempt against its rolling window.</summary>
    public bool TryAdmitProviderAttempt(Guid providerId, int requestsPerMinute) =>
        _providerWindows.GetOrAdd(providerId, _ => new SlidingWindow())
            .TryTake(requestsPerMinute, _timeProvider.GetUtcNow());

    /// <summary>Admits one user generation and acquires provider/global concurrency.</summary>
    public async Task<RoutingBudgetLease?> AcquireAsync(
        string userId, Guid providerId, int providerRequestsPerMinute, int providerMaxConcurrency, CancellationToken cancellationToken)
    {
        if (!TryAdmitUserGeneration(userId))
            return null;
        if (!await _global.WaitAsync(0, cancellationToken)) return null;
        var providerGate = _providerConcurrency.GetOrAdd(providerId, _ => new ProviderGate());
        if (!providerGate.TryAcquire(providerMaxConcurrency))
        {
            _global.Release();
            return null;
        }
        return new RoutingBudgetLease(_global, providerGate.Release,
            () => _providerWindows.GetOrAdd(providerId, _ => new SlidingWindow())
                .TryTake(providerRequestsPerMinute, _timeProvider.GetUtcNow()));
    }

    /// <summary>Applies the same durable provider admission to administrator verification probes.</summary>
    public async Task<RoutingBudgetLease?> AcquireProviderAsync(
        Guid providerId, int requestsPerMinute, int maxConcurrency, CancellationToken cancellationToken)
    {
        if (!await _global.WaitAsync(0, cancellationToken)) return null;
        var providerGate = _providerConcurrency.GetOrAdd(providerId, _ => new ProviderGate());
        if (!providerGate.TryAcquire(maxConcurrency)) { _global.Release(); return null; }
        return new RoutingBudgetLease(_global, providerGate.Release,
            () => _providerWindows.GetOrAdd(providerId, _ => new SlidingWindow())
                .TryTake(requestsPerMinute, _timeProvider.GetUtcNow()));
    }

    private sealed class ProviderGate
    {
        private readonly object _sync = new();
        private int _active;

        public bool TryAcquire(int capacity)
        {
            lock (_sync)
            {
                if (_active >= capacity) return false;
                _active++;
                return true;
            }
        }

        public void Release()
        {
            lock (_sync)
            {
                if (_active == 0) throw new InvalidOperationException("The provider gate has no active lease.");
                _active--;
            }
        }
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

/// <summary>Holds global and provider capacity for exactly one DNS/send/response attempt.</summary>
public sealed class RoutingConcurrencyLease : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly Action _releaseProvider;
    private int _disposed;

    internal RoutingConcurrencyLease(SemaphoreSlim global, Action releaseProvider)
        => (_global, _releaseProvider) = (global, releaseProvider);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _releaseProvider();
        _global.Release();
    }
}

/// <summary>Holds concurrency and admits each initial/retry provider request against one shared budget.</summary>
public sealed class RoutingBudgetLease : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly Action _releaseProvider;
    private readonly Func<bool> _admitAttempt;
    private int _disposed;

    internal RoutingBudgetLease(SemaphoreSlim global, Action releaseProvider, Func<bool> admitAttempt)
        => (_global, _releaseProvider, _admitAttempt) = (global, releaseProvider, admitAttempt);

    /// <summary>Charges one actual provider attempt, including the one permitted retry.</summary>
    public bool TryAdmitProviderAttempt() => _admitAttempt();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _releaseProvider();
        _global.Release();
    }
}
