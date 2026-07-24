using Wayfarer.Areas.Public.Controllers;

namespace Wayfarer.Services;

/// <summary>Distinguishes visible work from maintenance at provider-capacity acquisition.</summary>
internal enum TileWorkPriority
{
    Foreground,
    Background
}

/// <summary>
/// Owns bounded foreground cold-tile admission, per-client fairness, coalescing, and cancellation.
/// Provider transport rate and retry policy remain owned by the existing outbound components.
/// </summary>
internal static class TileWorkScheduler
{
    internal const int ForegroundQueueCapacity = 64;
    internal const int BackgroundQueueCapacity = 16;
    internal const int ForegroundConcurrency = 12;
    internal const int PerClientConcurrency = 6;
    internal const int PerClientQueueCapacity = 24;
    internal static readonly TimeSpan ForegroundQueueWait = TimeSpan.FromSeconds(30);

    private static readonly object _sync = new();
    private static readonly Dictionary<string, ColdFlight> _flights = new(StringComparer.Ordinal);
    private static readonly LinkedList<ColdFlight> _foregroundQueue = new();
    private static readonly Dictionary<string, int> _activeByClient = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _waitingByClient = new(StringComparer.Ordinal);
    private static int _activeForeground;
    private static int _waitingForeground;
    private static bool _stopping;

    /// <summary>Indicates whether accepted visible work is waiting for an execution slot.</summary>
    internal static bool HasQueuedForeground
    {
        get
        {
            lock (_sync)
            {
                return _foregroundQueue.Count > 0;
            }
        }
    }

    /// <summary>Joins or creates one provider-scoped cold flight with caller-owned cancellation.</summary>
    internal static Task<TileRetrievalResult> ExecuteForegroundAsync(
        string workKey,
        string clientKey,
        Func<CancellationToken, Task<TileRetrievalResult>> operation,
        CancellationToken cancellationToken) =>
        ExecuteForegroundCoreAsync(
            workKey, clientKey, operation, waitingLease: null, cancellationToken);

    /// <summary>Executes admission while optionally transferring an existing waiting lease.</summary>
    private static Task<TileRetrievalResult> ExecuteForegroundCoreAsync(
        string workKey,
        string clientKey,
        Func<CancellationToken, Task<TileRetrievalResult>> operation,
        WaitingLease? waitingLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ColdFlight flight;
        var startNow = false;
        lock (_sync)
        {
            if (_stopping)
            {
                waitingLease?.ReleaseLocked();
                return Rejected();
            }

            if (_flights.TryGetValue(workKey, out flight!))
            {
                waitingLease ??= TryAcquireWaitingLease(clientKey);
                if (waitingLease == null)
                {
                    return Rejected();
                }

                if (flight.Retiring)
                {
                    return WaitForRetirementAndRetryAsync(
                        flight, clientKey, operation, waitingLease, cancellationToken);
                }

                flight.WaiterCount++;
            }
            else
            {
                startNow = _foregroundQueue.Count == 0 && CanStart(clientKey);
                if (startNow)
                {
                    waitingLease?.ReleaseLocked();
                    waitingLease = null;
                }
                else
                {
                    if (_foregroundQueue.Count >= ForegroundQueueCapacity)
                    {
                        waitingLease?.ReleaseLocked();
                        return Rejected();
                    }

                    waitingLease ??= TryAcquireWaitingLease(clientKey);
                    if (waitingLease == null)
                    {
                        return Rejected();
                    }
                }

                flight = new ColdFlight(workKey, clientKey, operation);
                _flights.Add(workKey, flight);
                if (startNow)
                {
                    MarkStarted(flight);
                }
                else
                {
                    flight.QueueNode = _foregroundQueue.AddLast(flight);
                    flight.LeaderWaitingLease = waitingLease;
                    _ = ExpireQueuedFlightAsync(flight);
                }
            }
        }

        var waiter = new FlightWaiter(flight, clientKey, operation, waitingLease);
        var task = WaitForFlightAsync(waiter, cancellationToken);
        if (startNow)
        {
            _ = RunFlightAsync(flight);
        }

        return task;
    }

    /// <summary>Retains one waiting admission until former ownership is fully unpublished.</summary>
    private static async Task<TileRetrievalResult> WaitForRetirementAndRetryAsync(
        ColdFlight flight,
        string clientKey,
        Func<CancellationToken, Task<TileRetrievalResult>> operation,
        WaitingLease? waitingLease,
        CancellationToken cancellationToken)
    {
        try
        {
            await flight.Termination.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            waitingLease?.Release();
            throw;
        }

        return await ExecuteForegroundCoreAsync(
                flight.WorkKey, clientKey, operation, waitingLease, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stops admission, cancels owned work, and completes when all runners terminate.</summary>
    internal static async Task StopAndDrainAsync()
    {
        ColdFlight[] flights;
        List<ColdFlight> queued = [];
        lock (_sync)
        {
            _stopping = true;
            flights = _flights.Values.ToArray();
            foreach (var flight in flights)
            {
                flight.Retiring = true;
                if (flight.QueueNode == null)
                {
                    continue;
                }

                _foregroundQueue.Remove(flight.QueueNode);
                flight.QueueNode = null;
                flight.Completed = true;
                flight.LeaderWaitingLease?.ReleaseLocked();
                queued.Add(flight);
            }
        }

        foreach (var flight in flights)
        {
            flight.SharedCancellation.Cancel();
        }

        foreach (var flight in queued)
        {
            PublishAndRemove(flight, result: null, error: new OperationCanceledException(
                flight.SharedCancellation.Token));
        }

        await Task.WhenAll(flights.Select(flight => flight.Termination.Task))
            .ConfigureAwait(false);
    }

    /// <summary>Cancels and clears scheduler state between isolated tests.</summary>
    internal static void ResetForTesting()
    {
        ColdFlight[] flights;
        lock (_sync)
        {
            flights = _flights.Values.ToArray();
            _flights.Clear();
            _foregroundQueue.Clear();
            _activeByClient.Clear();
            _waitingByClient.Clear();
            _activeForeground = 0;
            _waitingForeground = 0;
            _stopping = false;
        }

        foreach (var flight in flights)
        {
            flight.SharedCancellation.Cancel();
            flight.Completion.TrySetCanceled(flight.SharedCancellation.Token);
            flight.Termination.TrySetResult();
        }
    }

    /// <summary>Awaits shared completion without linking one caller's token to transport.</summary>
    private static async Task<TileRetrievalResult> WaitForFlightAsync(
        FlightWaiter waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await waiter.Flight.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                ReleaseCancelledWaiter(waiter);
                throw new OperationCanceledException(cancellationToken);
            }

            waiter.WaitingLease?.Release();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseCancelledWaiter(waiter);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (waiter.Flight.SharedCancellation.IsCancellationRequested)
        {
            return await WaitForRetirementAndRetryAsync(
                    waiter.Flight,
                    waiter.ClientKey,
                    waiter.Operation,
                    waiter.WaitingLease,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            waiter.WaitingLease?.Release();
            throw;
        }
    }

    /// <summary>Cancels transport only after the final interested waiter leaves.</summary>
    private static void ReleaseCancelledWaiter(FlightWaiter waiter)
    {
        ColdFlight? abandonedQueued = null;
        var cancelShared = false;
        lock (_sync)
        {
            waiter.WaitingLease?.ReleaseLocked();
            var flight = waiter.Flight;
            if (flight.Completed || flight.WaiterCount == 0)
            {
                return;
            }

            flight.WaiterCount--;
            if (flight.WaiterCount != 0)
            {
                return;
            }

            flight.Retiring = true;
            if (flight.QueueNode != null)
            {
                _foregroundQueue.Remove(flight.QueueNode);
                flight.QueueNode = null;
                flight.Completed = true;
                flight.LeaderWaitingLease?.ReleaseLocked();
                abandonedQueued = flight;
            }

            cancelShared = true;
        }

        if (cancelShared)
        {
            waiter.Flight.SharedCancellation.Cancel();
        }

        if (abandonedQueued != null)
        {
            PublishAndRemove(
                abandonedQueued,
                result: null,
                error: new OperationCanceledException(abandonedQueued.SharedCancellation.Token));
            DrainQueue();
        }
    }

    /// <summary>Runs one admitted flight and publishes completion before releasing its key.</summary>
    private static async Task RunFlightAsync(ColdFlight flight)
    {
        TileRetrievalResult? result = null;
        Exception? error = null;
        try
        {
            result = await flight.Operation(flight.SharedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        lock (_sync)
        {
            flight.Completed = true;
            _activeForeground = Math.Max(0, _activeForeground - 1);
            Decrement(_activeByClient, flight.ClientKey);
        }

        PublishAndRemove(flight, result, error);
        DrainQueue();
    }

    /// <summary>Expires one queued flight without exposing a remove-before-publish interval.</summary>
    private static async Task ExpireQueuedFlightAsync(ColdFlight flight)
    {
        await Task.Delay(ForegroundQueueWait).ConfigureAwait(false);
        var expired = false;
        lock (_sync)
        {
            if (flight.Completed || flight.QueueNode == null)
            {
                return;
            }

            _foregroundQueue.Remove(flight.QueueNode);
            flight.QueueNode = null;
            flight.Completed = true;
            flight.LeaderWaitingLease?.ReleaseLocked();
            expired = true;
        }

        if (expired)
        {
            PublishAndRemove(
                flight,
                TileRetrievalResult.Throttled(TilesController.BudgetRetryAfterSeconds),
                error: null);
            DrainQueue();
        }
    }

    /// <summary>Publishes the terminal outcome before allowing a new owner for the same key.</summary>
    private static void PublishAndRemove(
        ColdFlight flight,
        TileRetrievalResult? result,
        Exception? error)
    {
        if (error is OperationCanceledException)
        {
            flight.Completion.TrySetCanceled(flight.SharedCancellation.Token);
        }
        else if (error != null)
        {
            flight.Completion.TrySetException(error);
        }
        else
        {
            flight.Completion.TrySetResult(result!);
        }

        lock (_sync)
        {
            RemoveFlight(flight);
        }

        flight.Termination.TrySetResult();
    }

    /// <summary>Starts the earliest eligible jobs while retaining unrelated-key progress.</summary>
    private static void DrainQueue()
    {
        List<ColdFlight> toStart = [];
        lock (_sync)
        {
            if (_stopping)
            {
                return;
            }

            while (_activeForeground < ForegroundConcurrency && _foregroundQueue.Count > 0)
            {
                var node = _foregroundQueue.First;
                while (node != null && !CanStart(node.Value.ClientKey))
                {
                    node = node.Next;
                }

                if (node == null)
                {
                    break;
                }

                var flight = node.Value;
                _foregroundQueue.Remove(node);
                flight.QueueNode = null;
                flight.LeaderWaitingLease?.ReleaseLocked();
                MarkStarted(flight);
                toStart.Add(flight);
            }
        }

        foreach (var flight in toStart)
        {
            _ = RunFlightAsync(flight);
        }
    }

    private static bool CanStart(string clientKey) =>
        _activeForeground < ForegroundConcurrency &&
        GetCount(_activeByClient, clientKey) < PerClientConcurrency;

    private static void MarkStarted(ColdFlight flight)
    {
        _activeForeground++;
        Increment(_activeByClient, flight.ClientKey);
    }

    private static WaitingLease? TryAcquireWaitingLease(string clientKey)
    {
        if (_waitingForeground >= ForegroundQueueCapacity ||
            GetCount(_waitingByClient, clientKey) >= PerClientQueueCapacity)
        {
            return null;
        }

        _waitingForeground++;
        Increment(_waitingByClient, clientKey);
        return new WaitingLease(clientKey);
    }

    private static Task<TileRetrievalResult> Rejected() =>
        Task.FromResult(TileRetrievalResult.Throttled(TilesController.BudgetRetryAfterSeconds));

    private static int GetCount(Dictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out var count) ? count : 0;

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = GetCount(counts, key) + 1;

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        var remaining = GetCount(counts, key) - 1;
        if (remaining <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = remaining;
        }
    }

    private static void RemoveFlight(ColdFlight flight)
    {
        if (_flights.TryGetValue(flight.WorkKey, out var current) &&
            ReferenceEquals(current, flight))
        {
            _flights.Remove(flight.WorkKey);
        }
    }

    /// <summary>Represents one request's bounded waiting admission.</summary>
    private sealed class WaitingLease
    {
        private readonly string _clientKey;
        private int _released;

        internal WaitingLease(string clientKey) => _clientKey = clientKey;

        internal void Release()
        {
            lock (_sync)
            {
                ReleaseLocked();
            }
        }

        internal void ReleaseLocked()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _waitingForeground = Math.Max(0, _waitingForeground - 1);
            Decrement(_waitingByClient, _clientKey);
        }
    }

    /// <summary>Captures request-specific state while it awaits a shared flight.</summary>
    private sealed record FlightWaiter(
        ColdFlight Flight,
        string ClientKey,
        Func<CancellationToken, Task<TileRetrievalResult>> Operation,
        WaitingLease? WaitingLease);

    /// <summary>Stores bounded shared state for one provider/tile fetch series.</summary>
    private sealed class ColdFlight
    {
        internal string WorkKey { get; }
        internal string ClientKey { get; }
        internal Func<CancellationToken, Task<TileRetrievalResult>> Operation { get; }
        internal CancellationTokenSource SharedCancellation { get; } = new();
        internal TaskCompletionSource<TileRetrievalResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Termination { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<ColdFlight>? QueueNode { get; set; }
        internal WaitingLease? LeaderWaitingLease { get; set; }
        internal int WaiterCount { get; set; } = 1;
        internal bool Completed { get; set; }
        internal bool Retiring { get; set; }

        internal ColdFlight(
            string workKey,
            string clientKey,
            Func<CancellationToken, Task<TileRetrievalResult>> operation)
        {
            WorkKey = workKey;
            ClientKey = clientKey;
            Operation = operation;
        }
    }
}
