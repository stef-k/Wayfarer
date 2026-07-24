using System.Diagnostics;
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
        internal const int BurstCapacity = 12;

        /// <summary>Current interval between sustained outbound token replenishments.</summary>
        internal const int ReplenishIntervalMs = 500;

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
                    acquired = _foregroundWaiters == 0 && _tokens.Wait(0);
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
                    _foregroundWaiters--;
                }
            }

            stopwatch.Stop();
            return new OutboundBudgetAcquisition(acquiredForeground, stopwatch.Elapsed);
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

        /// <summary>Stops replenishment during application shutdown.</summary>
        internal static void Stop() => _replenisherCts.Cancel();

        /// <summary>Restores burst capacity and production acquisition for an isolated test.</summary>
        internal static void ResetForTesting()
        {
            _acquireOverride = null;
            Volatile.Write(ref _foregroundWaiters, 0);
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
    }
}
