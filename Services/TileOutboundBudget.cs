using System.Diagnostics;
using System.Collections.Concurrent;
using Wayfarer.Services;

public partial class TileCacheService
{
    /// <summary>
    /// Enforces Wayfarer's current conservative global outbound tile-safety budget.
    /// These constants characterize existing behavior and are not claims about provider policy.
    /// </summary>
    internal static class OutboundBudget
    {
        /// <summary>Maximum number of immediate outbound acquisitions.</summary>
        internal const int BurstCapacity = 20;

        /// <summary>Current interval between sustained outbound token replenishments.</summary>
        internal const int ReplenishIntervalMs = 167;

        /// <summary>Maximum accepted foreground wait before bounded scheduler rejection.</summary>
        internal static readonly TimeSpan AcquireTimeout = TileWorkScheduler.ForegroundQueueWait;

        /// <summary>Available outbound tokens shared by all scoped tile services.</summary>
        private static readonly SemaphoreSlim _tokens = new(BurstCapacity, BurstCapacity);

        /// <summary>Optional deterministic acquisition implementation used only by controlled tests.</summary>
        private static Func<CancellationToken, Task<OutboundBudgetAcquisition>>? _acquireOverride;

        /// <summary>Stops the current replenisher without disposing state it may still reference.</summary>
        private static volatile CancellationTokenSource _replenisherCts = new();

        /// <summary>Starts exactly one replenisher for the current cancellation source.</summary>
        private static volatile Lazy<Task> _replenisher = new(
            () => StartReplenisher(_replenisherCts.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Serializes replenisher replacement so two loops cannot overlap.</summary>
        private static readonly object _stopLock = new();
        private static readonly object _priorityLock = new();
        private static int _foregroundWaiters;
        private static int _backgroundContactActive;
        private static readonly ConcurrentDictionary<string, ProviderBudgetState> _providerStates = new();
        private static readonly object _providerStateLock = new();
        private const int MaximumRetainedProviderStates = 32;

        /// <summary>
        /// Reserves the single background transport slot without waiting or displacing foreground work.
        /// </summary>
        internal static IDisposable? TryAcquireBackgroundContact()
        {
            return Interlocked.CompareExchange(ref _backgroundContactActive, 1, 0) == 0
                ? new BackgroundContactLease()
                : null;
        }

        /// <summary>Attempts to acquire capacity under the current production budget.</summary>
        internal static async Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
        {
            var acquisition = await AcquireDetailedAsync(cancellationToken).ConfigureAwait(false);
            return acquisition.Acquired;
        }

        /// <summary>Acquires capacity and returns test-observable wait evidence.</summary>
        internal static async Task<OutboundBudgetAcquisition> AcquireDetailedAsync(
            CancellationToken cancellationToken = default,
            TileWorkPriority priority = TileWorkPriority.Foreground)
        {
            var acquireOverride = _acquireOverride;
            if (acquireOverride != null)
            {
                return await acquireOverride(cancellationToken).ConfigureAwait(false);
            }

            _ = _replenisher.Value;
            var stopwatch = Stopwatch.StartNew();
            if (priority == TileWorkPriority.Background)
            {
                bool acquired;
                lock (_priorityLock)
                {
                    acquired = _foregroundWaiters == 0 &&
                               !TileWorkScheduler.HasQueuedForeground &&
                               _tokens.Wait(0);
                }

                stopwatch.Stop();
                return new OutboundBudgetAcquisition(acquired, stopwatch.Elapsed);
            }

            lock (_priorityLock)
            {
                _foregroundWaiters++;
            }

            bool acquiredForeground;
            try
            {
                acquiredForeground = await _tokens
                    .WaitAsync(AcquireTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_priorityLock)
                {
                    if (_foregroundWaiters > 0)
                    {
                        _foregroundWaiters--;
                    }
                }
            }

            stopwatch.Stop();
            return new OutboundBudgetAcquisition(acquiredForeground, stopwatch.Elapsed);
        }

        /// <summary>
        /// Acquires one profile rate token and one authoritative application concurrency slot.
        /// The returned lease releases concurrency only; consumed rate tokens replenish over time.
        /// </summary>
        internal static async Task<ProviderContactLease?> AcquireProviderContactAsync(
            TileProviderPolicy profile,
            TileWorkPriority priority,
            CancellationToken cancellationToken)
        {
            var acquireOverride = _acquireOverride;
            if (acquireOverride != null)
            {
                var controlled = await acquireOverride(cancellationToken).ConfigureAwait(false);
                return controlled.Acquired ? ProviderContactLease.Controlled : null;
            }

            var stateKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{profile.Identity}|{profile.SustainedRequestsPerSecond}|{profile.BurstCapacity}|{profile.MaxConcurrency}");
            var state = AcquireProviderStateReference(stateKey, profile);
            if (state == null)
            {
                return null;
            }

            var ownershipTransferred = false;
            try
            {
                if (!await state.AcquireRateAsync(priority, cancellationToken).ConfigureAwait(false) ||
                    !await state.AcquireConcurrencyAsync(priority, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                ownershipTransferred = true;
                return new ProviderContactLease(state);
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    ReleaseProviderStateReference(state);
                }
            }
        }

        /// <summary>Atomically finds or admits one referenced provider state.</summary>
        private static ProviderBudgetState? AcquireProviderStateReference(
            string stateKey,
            TileProviderPolicy profile)
        {
            lock (_providerStateLock)
            {
                if (_providerStates.TryGetValue(stateKey, out var existing))
                {
                    existing.AddReference();
                    return existing;
                }

                while (_providerStates.Count >= MaximumRetainedProviderStates &&
                       RetireOneIdleProviderState())
                {
                }

                if (_providerStates.Count >= MaximumRetainedProviderStates)
                {
                    return null;
                }

                var admitted = new ProviderBudgetState(stateKey, profile);
                admitted.AddReference();
                _providerStates[stateKey] = admitted;
                return admitted;
            }
        }

        /// <summary>Releases ownership and performs bounded retirement when capacity is full.</summary>
        private static void ReleaseProviderStateReference(ProviderBudgetState state)
        {
            lock (_providerStateLock)
            {
                state.ReleaseReference();
                if (_providerStates.Count >= MaximumRetainedProviderStates)
                {
                    RetireOneIdleProviderState();
                }
            }
        }

        /// <summary>Compare-removes and stops the oldest exact idle, unreferenced state.</summary>
        private static bool RetireOneIdleProviderState()
        {
            var candidate = _providerStates
                .Where(pair => pair.Value.IsRetirable)
                .OrderBy(pair => pair.Value.LastUsedUtc)
                .FirstOrDefault();
            if (candidate.Value == null ||
                !_providerStates.TryRemove(candidate))
            {
                return false;
            }

            candidate.Value.Dispose();
            return true;
        }

        /// <summary>Releases one token per current replenishment interval up to burst capacity.</summary>
        private static Task StartReplenisher(CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ReplenishIntervalMs));
                try
                {
                    while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (_tokens.CurrentCount >= BurstCapacity)
                        {
                            continue;
                        }

                        try
                        {
                            _tokens.Release();
                        }
                        catch (SemaphoreFullException)
                        {
                            // Another thread won the harmless release race.
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown and isolated test reset.
                }
            }, CancellationToken.None);

        /// <summary>Cancels and replaces the replenisher state without overlapping loops.</summary>
        private static void StopReplenisher()
        {
            lock (_stopLock)
            {
                var oldCts = _replenisherCts;
                oldCts.Cancel();
                var newCts = new CancellationTokenSource();
                _replenisherCts = newCts;
                _replenisher = new Lazy<Task>(
                    () => StartReplenisher(newCts.Token),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        /// <summary>Stops global and per-provider replenishment during application shutdown.</summary>
        internal static void Stop()
        {
            _replenisherCts.Cancel();
            lock (_providerStateLock)
            {
                foreach (var state in _providerStates.Values)
                {
                    state.Stop();
                }
            }
        }

        /// <summary>Restores burst capacity and production acquisition for an isolated test.</summary>
        internal static void ResetForTesting()
        {
            _acquireOverride = null;
            lock (_priorityLock)
            {
                _foregroundWaiters = 0;
            }
            Interlocked.Exchange(ref _backgroundContactActive, 0);
            lock (_providerStateLock)
            {
                foreach (var state in _providerStates.Values)
                {
                    state.Dispose();
                }
                _providerStates.Clear();
            }
            StopReplenisher();
            while (_tokens.CurrentCount > 0)
            {
                _tokens.Wait(0);
            }

            try
            {
                _tokens.Release(BurstCapacity);
            }
            catch (SemaphoreFullException)
            {
                // Capacity was already restored by a harmless concurrent release.
            }
        }

        /// <summary>Drains capacity and prevents replenishment for an isolated rejection test.</summary>
        internal static void DrainForTesting()
        {
            StopReplenisher();
            while (_tokens.CurrentCount > 0)
            {
                _tokens.Wait(0);
            }

            _replenisherCts.Cancel();
        }

        /// <summary>Overrides acquisition without changing any production budget constant.</summary>
        internal static void SetAcquireOverrideForTesting(
            Func<CancellationToken, Task<OutboundBudgetAcquisition>>? acquireOverride) =>
            _acquireOverride = acquireOverride;

        /// <summary>Releases one controlled token for deterministic priority tests.</summary>
        internal static void ReleaseOneForTesting()
        {
            if (_tokens.CurrentCount < BurstCapacity)
            {
                _tokens.Release();
            }
        }

        /// <summary>Releases the single background transport slot exactly once.</summary>
        private sealed class BackgroundContactLease : IDisposable
        {
            private int _disposed;

            /// <inheritdoc />
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Exchange(ref _backgroundContactActive, 0);
                }
            }
        }

        /// <summary>Gets the retained provider-state count for focused invariant tests.</summary>
        internal static int ProviderStateCountForTesting
        {
            get
            {
                lock (_providerStateLock)
                {
                    return _providerStates.Count;
                }
            }
        }

        /// <summary>Releases one application-level provider concurrency slot.</summary>
        internal sealed class ProviderContactLease : IDisposable
        {
            private ProviderBudgetState? _state;
            internal static ProviderContactLease Controlled => new(null);
            internal ProviderContactLease(ProviderBudgetState? state) => _state = state;
            public void Dispose()
            {
                var state = Interlocked.Exchange(ref _state, null);
                if (state == null)
                {
                    return;
                }

                state.ReleaseConcurrency();
                ReleaseProviderStateReference(state);
            }
        }

        /// <summary>Owns rate and concurrency state for one immutable profile version.</summary>
        internal sealed class ProviderBudgetState : IDisposable
        {
            private readonly SemaphoreSlim _tokens;
            private readonly SemaphoreSlim _concurrency;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _replenisher;
            private int _activeContacts;
            private int _references;
            private readonly string _stateKey;

            internal ProviderBudgetState(string stateKey, TileProviderPolicy profile)
            {
                _stateKey = stateKey;
                _tokens = new SemaphoreSlim(profile.BurstCapacity, profile.BurstCapacity);
                _concurrency = new SemaphoreSlim(profile.MaxConcurrency, profile.MaxConcurrency);
                var interval = TimeSpan.FromSeconds(1d / profile.SustainedRequestsPerSecond);
                _replenisher = Task.Run(() => ReplenishAsync(interval, profile.BurstCapacity));
            }

            internal DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
            internal bool IsRetirable =>
                Volatile.Read(ref _references) == 0 &&
                Volatile.Read(ref _activeContacts) == 0 &&
                _providerStates.TryGetValue(_stateKey, out var current) &&
                ReferenceEquals(current, this);
            internal void Touch() => LastUsedUtc = DateTime.UtcNow;
            internal void AddReference()
            {
                _references++;
                Touch();
            }

            internal void ReleaseReference()
            {
                _references--;
                Touch();
            }

            internal async Task<bool> AcquireRateAsync(
                TileWorkPriority priority,
                CancellationToken cancellationToken)
            {
                if (priority == TileWorkPriority.Background)
                {
                    return !TileWorkScheduler.HasQueuedForeground && _tokens.Wait(0);
                }

                return await _tokens.WaitAsync(AcquireTimeout, cancellationToken).ConfigureAwait(false);
            }

            internal async Task<bool> AcquireConcurrencyAsync(
                TileWorkPriority priority,
                CancellationToken cancellationToken)
            {
                var acquired = priority == TileWorkPriority.Background
                    ? _concurrency.Wait(0)
                    : await _concurrency.WaitAsync(AcquireTimeout, cancellationToken).ConfigureAwait(false);
                if (acquired)
                {
                    Interlocked.Increment(ref _activeContacts);
                }
                return acquired;
            }

            internal void ReleaseConcurrency()
            {
                Interlocked.Decrement(ref _activeContacts);
                _concurrency.Release();
                Touch();
            }

            private async Task ReplenishAsync(TimeSpan interval, int capacity)
            {
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    {
                        if (_tokens.CurrentCount < capacity)
                        {
                            try { _tokens.Release(); } catch (SemaphoreFullException) { }
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }

            public void Dispose()
            {
                Stop();
                _stop.Dispose();
            }

            internal void Stop() => _stop.Cancel();
        }
    }
}
