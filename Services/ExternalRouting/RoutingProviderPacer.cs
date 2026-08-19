using System.Collections.Concurrent;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns deterministic FIFO minimum-interval pacing for each provider in this process.</summary>
public sealed class RoutingProviderPacer
{
    /// <summary>Maximum waiters behind or including the current queue head.</summary>
    public const int MaximumQueuedWaiters = 32;
    /// <summary>Maximum time one attempt may remain in the pacing queue.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(120);
    /// <summary>Minimum idle duration before unused provider state may be retired.</summary>
    public static readonly TimeSpan MinimumIdleLifetime = TimeSpan.FromMinutes(5);

    private const int CleanupScanLimit = 8;
    private readonly ConcurrentDictionary<Guid, ProviderGate> _gates = new();
    private readonly object _gateLookupSync = new();
    private readonly TimeProvider _timeProvider;
    private int _cleanupCursor;

    /// <summary>Initializes process-local pacing with an injectable monotonic time authority.</summary>
    public RoutingProviderPacer(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <summary>Applies a committed interval only when its configuration version is current or newer.</summary>
    public bool ApplyConfiguration(Guid providerId, int configurationVersion, int intervalMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(configurationVersion);
        if (intervalMilliseconds is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        var gate = GetGate(providerId);
        try
        {
            lock (gate.Sync)
            {
                if (configurationVersion < gate.ConfigurationVersion) return false;
                gate.ConfigurationVersion = configurationVersion;
                gate.IntervalMilliseconds = intervalMilliseconds;
                gate.ConfigurationChanged.Cancel();
                gate.ConfigurationChanged.Dispose();
                gate.ConfigurationChanged = new CancellationTokenSource();
                gate.PulseHead();
                return true;
            }
        }
        finally { ReleaseAccessor(gate); }
    }

    /// <summary>Waits for the provider's atomic pacing turn without holding concurrency permits.</summary>
    public async Task<RoutingPacingResult> WaitAsync(
        Guid providerId, int expectedConfigurationVersion, CancellationToken cancellationToken)
    {
        var gate = GetGate(providerId);
        var waiter = new Waiter(_timeProvider.GetTimestamp());
        try
        {
            lock (gate.Sync)
            {
                if (gate.ConfigurationVersion != expectedConfigurationVersion)
                    return RoutingPacingResult.Failure("provider-configuration-stale");
                if (gate.Waiters.Count >= MaximumQueuedWaiters)
                    return RoutingPacingResult.Failure("routing-rate-limited");
                waiter.Node = gate.Waiters.AddLast(waiter);
                gate.PulseHead();
            }
        }
        finally { ReleaseAccessor(gate); }

        using var callerRegistration = cancellationToken.Register(() => CancelWaiter(gate, waiter));
        using var timeout = new CancellationTokenSource();
        var timeoutTask = Task.Delay(MaximumWait, _timeProvider, timeout.Token);
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelWaiter(gate, waiter);
                    return RoutingPacingResult.Failure("request-cancelled");
                }
                if (_timeProvider.GetElapsedTime(waiter.EnqueuedTimestamp) >= MaximumWait)
                {
                    CancelWaiter(gate, waiter);
                    return RoutingPacingResult.Failure("routing-timeout");
                }
                var headTask = waiter.Head.Task;
                var completed = await Task.WhenAny(headTask, timeoutTask);
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelWaiter(gate, waiter);
                    return RoutingPacingResult.Failure("request-cancelled");
                }
                if (_timeProvider.GetElapsedTime(waiter.EnqueuedTimestamp) >= MaximumWait)
                {
                    CancelWaiter(gate, waiter);
                    return RoutingPacingResult.Failure("routing-timeout");
                }
                await headTask;

                TimeSpan remaining;
                CancellationToken changed;
                lock (gate.Sync)
                {
                    if (waiter.Node?.List == null) return RoutingPacingResult.Failure("request-cancelled");
                    remaining = gate.Remaining(_timeProvider);
                    if (remaining <= TimeSpan.Zero && !gate.Active)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Remove(gate, waiter);
                            return RoutingPacingResult.Failure("request-cancelled");
                        }
                        if (_timeProvider.GetElapsedTime(waiter.EnqueuedTimestamp) >= MaximumWait)
                        {
                            Remove(gate, waiter);
                            return RoutingPacingResult.Failure("routing-timeout");
                        }
                        gate.Active = true;
                        gate.Waiters.Remove(waiter.Node);
                        waiter.Node = null;
                        timeout.Cancel();
                        return new RoutingPacingResult(true, null,
                            new RoutingPacingTurn(gate, _timeProvider, waiter.EnqueuedTimestamp));
                    }
                    changed = gate.ConfigurationChanged.Token;
                    waiter.Head = NewSignal();
                }
                using var reevaluate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, changed);
                try
                {
                    var deadlineRemaining = MaximumWait - _timeProvider.GetElapsedTime(waiter.EnqueuedTimestamp);
                    await Task.Delay(remaining < deadlineRemaining ? remaining : deadlineRemaining, _timeProvider, reevaluate.Token);
                }
                catch (OperationCanceledException) when (changed.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
                lock (gate.Sync) gate.PulseHead();
            }
        }
        catch (OperationCanceledException)
        {
            CancelWaiter(gate, waiter);
            return RoutingPacingResult.Failure("request-cancelled");
        }
    }

    /// <summary>Opportunistically retires a bounded number of safely idle gates.</summary>
    internal int CleanupIdle()
    {
        lock (_gateLookupSync)
        {
            var removed = 0;
            var candidates = _gates.ToArray();
            if (candidates.Length == 0) return 0;
            var start = Math.Abs(Interlocked.Increment(ref _cleanupCursor));
            for (var index = 0; index < Math.Min(CleanupScanLimit, candidates.Length); index++)
            {
                var candidate = candidates[(start + index) % candidates.Length];
                lock (candidate.Value.Sync)
                {
                    if (candidate.Value.Accessors == 0 && !candidate.Value.Active && candidate.Value.Waiters.Count == 0
                        && _timeProvider.GetElapsedTime(candidate.Value.LastIdleTimestamp) >= MinimumIdleLifetime
                        && _gates.TryRemove(candidate)) removed++;
                }
            }
            return removed;
        }
    }

    internal int GateCount => _gates.Count;

    private ProviderGate GetGate(Guid providerId)
    {
        CleanupIdle();
        lock (_gateLookupSync)
        {
            var gate = _gates.GetOrAdd(providerId, _ => new ProviderGate(_timeProvider.GetTimestamp()));
            lock (gate.Sync) gate.Accessors++;
            return gate;
        }
    }

    private static void ReleaseAccessor(ProviderGate gate)
    {
        lock (gate.Sync) gate.Accessors--;
    }

    private static void CancelWaiter(ProviderGate gate, Waiter waiter)
    {
        lock (gate.Sync) Remove(gate, waiter);
    }

    private static void Remove(ProviderGate gate, Waiter waiter)
    {
        if (waiter.Node?.List == null) return;
        var wasHead = waiter.Node == gate.Waiters.First;
        gate.Waiters.Remove(waiter.Node);
        waiter.Node = null;
        waiter.Head.TrySetCanceled();
        if (wasHead) gate.PulseHead();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class Waiter(long enqueuedTimestamp)
    {
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        public TaskCompletionSource Head { get; set; } = NewSignal();
        public LinkedListNode<Waiter>? Node { get; set; }
    }

    internal sealed class ProviderGate(long createdTimestamp)
    {
        internal object Sync { get; } = new();
        internal LinkedList<Waiter> Waiters { get; } = new();
        internal CancellationTokenSource ConfigurationChanged { get; set; } = new();
        internal int ConfigurationVersion { get; set; } = -1;
        internal int IntervalMilliseconds { get; set; } = 1000;
        internal bool Active { get; set; }
        internal int Accessors { get; set; }
        internal long? LastAttemptStart { get; set; }
        internal long LastIdleTimestamp { get; set; } = createdTimestamp;

        internal TimeSpan Remaining(TimeProvider timeProvider)
        {
            if (LastAttemptStart == null || IntervalMilliseconds == 0) return TimeSpan.Zero;
            var elapsed = timeProvider.GetElapsedTime(LastAttemptStart.Value);
            return TimeSpan.FromMilliseconds(IntervalMilliseconds) - elapsed;
        }

        internal void PulseHead()
        {
            if (!Active) Waiters.First?.Value.Head.TrySetResult();
        }
    }

    /// <summary>Owns one queue head until an attempt start is recorded or the turn is abandoned.</summary>
    public sealed class RoutingPacingTurn : IDisposable
    {
        private readonly ProviderGate _gate;
        private readonly TimeProvider _timeProvider;
        private readonly long _enqueuedTimestamp;
        private int _disposed;

        internal RoutingPacingTurn(ProviderGate gate, TimeProvider timeProvider, long enqueuedTimestamp)
            => (_gate, _timeProvider, _enqueuedTimestamp) = (gate, timeProvider, enqueuedTimestamp);

        /// <summary>Records the exact monotonic start immediately before DNS.</summary>
        public void RecordAttemptStart()
        {
            lock (_gate.Sync)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(RoutingPacingTurn));
                _gate.LastAttemptStart = _timeProvider.GetTimestamp();
            }
        }

        /// <summary>Atomically starts the network deadline, records pacing, releases the turn, and invokes DNS.</summary>
        internal string? StartAttempt(
            TimeSpan timeout, CancellationToken cancellationToken, Func<bool> admitRate,
            Action<CancellationToken> beginDns, out IDisposable? deadline)
        {
            deadline = null;
            CancellationTokenSource? source = null;
            ITimer? timer = null;
            lock (_gate.Sync)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(RoutingPacingTurn));
                if (cancellationToken.IsCancellationRequested) return "request-cancelled";
                if (_timeProvider.GetElapsedTime(_enqueuedTimestamp) >= MaximumWait) return "routing-timeout";
                if (!admitRate()) return "routing-rate-limited";
                source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timer = _timeProvider.CreateTimer(_ => source.Cancel(), null, timeout, Timeout.InfiniteTimeSpan);
                _gate.LastAttemptStart = _timeProvider.GetTimestamp();
                _disposed = 1;
                _gate.Active = false;
                _gate.LastIdleTimestamp = _timeProvider.GetTimestamp();
                _gate.PulseHead();
            }
            deadline = new AttemptDeadline(source, timer);
            beginDns(source.Token);
            return null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate.Sync)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _gate.Active = false;
                _gate.LastIdleTimestamp = _timeProvider.GetTimestamp();
                _gate.PulseHead();
            }
        }
    }

    private sealed class AttemptDeadline(CancellationTokenSource source, ITimer timer) : IDisposable
    {
        public void Dispose()
        {
            timer.Dispose();
            source.Dispose();
        }
    }
}

/// <summary>Contains a pacing turn or a bounded Wayfarer failure category.</summary>
public sealed record RoutingPacingResult(
    bool Succeeded, string? ErrorCode, RoutingProviderPacer.RoutingPacingTurn? Turn = null)
{
    /// <summary>Creates a failure without provider details.</summary>
    public static RoutingPacingResult Failure(string code) => new(false, code);
}
