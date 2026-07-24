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
    private static readonly Dictionary<string, int> _queuedByClient = new(StringComparer.Ordinal);
    private static int _activeForeground;

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

    /// <summary>
    /// Joins or creates one provider-scoped cold flight while retaining caller-owned cancellation.
    /// </summary>
    internal static Task<TileRetrievalResult> ExecuteForegroundAsync(
        string workKey,
        string clientKey,
        Func<CancellationToken, Task<TileRetrievalResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ColdFlight? flight;
        var startNow = false;
        lock (_sync)
        {
            if (_flights.TryGetValue(workKey, out flight))
            {
                flight.WaiterCount++;
            }
            else
            {
                var queuedForClient = GetCount(_queuedByClient, clientKey);
                if (_foregroundQueue.Count >= ForegroundQueueCapacity ||
                    queuedForClient >= PerClientQueueCapacity)
                {
                    return Task.FromResult(TileRetrievalResult.Throttled(
                        TilesController.BudgetRetryAfterSeconds));
                }

                flight = new ColdFlight(workKey, clientKey, operation);
                _flights.Add(workKey, flight);
                if (_foregroundQueue.Count == 0 && CanStart(clientKey))
                {
                    MarkStarted(flight);
                    startNow = true;
                }
                else
                {
                    flight.QueueNode = _foregroundQueue.AddLast(flight);
                    Increment(_queuedByClient, clientKey);
                    _ = ExpireQueuedFlightAsync(flight);
                }
            }
        }

        var waiter = WaitForFlightAsync(flight, cancellationToken);
        if (startNow)
        {
            _ = RunFlightAsync(flight);
        }

        return waiter;
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
            _queuedByClient.Clear();
            _activeForeground = 0;
        }

        foreach (var flight in flights)
        {
            flight.SharedCancellation.Cancel();
        }
    }

    /// <summary>Awaits shared completion without linking one caller's token to the shared transport.</summary>
    private static async Task<TileRetrievalResult> WaitForFlightAsync(
        ColdFlight flight,
        CancellationToken cancellationToken)
    {
        TileRetrievalResult result;
        try
        {
            result = await flight.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseCancelledWaiter(flight);
            throw new OperationCanceledException(cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseCancelledWaiter(flight);
            throw new OperationCanceledException(cancellationToken);
        }

        return result;
    }

    /// <summary>Removes queued work or cancels transport when its last interested waiter leaves.</summary>
    private static void ReleaseCancelledWaiter(ColdFlight flight)
    {
        var cancelShared = false;
        var drain = false;
        lock (_sync)
        {
            if (flight.Completed || flight.WaiterCount == 0)
            {
                return;
            }

            flight.WaiterCount--;
            if (flight.WaiterCount != 0)
            {
                return;
            }

            RemoveFlight(flight);
            if (flight.QueueNode != null)
            {
                _foregroundQueue.Remove(flight.QueueNode);
                flight.QueueNode = null;
                Decrement(_queuedByClient, flight.ClientKey);
                flight.Completed = true;
                drain = true;
            }

            cancelShared = true;
        }

        if (cancelShared)
        {
            flight.SharedCancellation.Cancel();
        }

        if (drain)
        {
            DrainQueue();
        }
    }

    /// <summary>Runs one admitted flight and releases its bounded concurrency ownership.</summary>
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
            RemoveFlight(flight);
            _activeForeground = Math.Max(0, _activeForeground - 1);
            Decrement(_activeByClient, flight.ClientKey);
        }

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

        DrainQueue();
    }

    /// <summary>Expires one queued flight without allowing an abandoned entry to remain shared.</summary>
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
            RemoveFlight(flight);
            Decrement(_queuedByClient, flight.ClientKey);
            expired = true;
        }

        if (expired)
        {
            flight.Completion.TrySetResult(TileRetrievalResult.Throttled(
                TilesController.BudgetRetryAfterSeconds));
            DrainQueue();
        }
    }

    /// <summary>Starts the earliest eligible jobs while respecting global and per-client caps.</summary>
    private static void DrainQueue()
    {
        List<ColdFlight> toStart = [];
        lock (_sync)
        {
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
                Decrement(_queuedByClient, flight.ClientKey);
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
        flight.Started = true;
        _activeForeground++;
        Increment(_activeByClient, flight.ClientKey);
    }

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

    /// <summary>Stores bounded shared state for one provider/tile fetch series.</summary>
    private sealed class ColdFlight
    {
        internal string WorkKey { get; }
        internal string ClientKey { get; }
        internal Func<CancellationToken, Task<TileRetrievalResult>> Operation { get; }
        internal CancellationTokenSource SharedCancellation { get; } = new();
        internal TaskCompletionSource<TileRetrievalResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<ColdFlight>? QueueNode { get; set; }
        internal int WaiterCount { get; set; } = 1;
        internal bool Started { get; set; }
        internal bool Completed { get; set; }

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
