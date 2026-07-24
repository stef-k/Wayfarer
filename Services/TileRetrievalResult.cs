namespace Wayfarer.Services;

/// <summary>
/// Classifies the production outcome of one tile retrieval operation.
/// </summary>
public enum TileRetrievalStatus
{
    /// <summary>Tile bytes were served from cache or a successful provider response.</summary>
    Success,

    /// <summary>The provider confirmed that the tile is permanently absent.</summary>
    NotFound,

    /// <summary>The provider returned a permanent response other than confirmed absence.</summary>
    PermanentFailure,

    /// <summary>A transient provider failure exhausted the bounded retry policy.</summary>
    TransientFailure,

    /// <summary>Local client allowance or global outbound capacity rejected the request.</summary>
    BudgetRejected
}

/// <summary>
/// Result of a tile retrieval attempt. Caller cancellation propagates as
/// <see cref="OperationCanceledException"/> and is never converted into a result status.
/// </summary>
/// <param name="Status">Typed production outcome used by the controller.</param>
/// <param name="TileData">Tile image bytes when retrieval succeeded, null otherwise.</param>
/// <param name="RetryAfterSeconds">Bounded client retry guidance for transient outcomes.</param>
public sealed record TileRetrievalResult(
    TileRetrievalStatus Status,
    byte[]? TileData = null,
    int? RetryAfterSeconds = null)
{
    /// <summary>Compatibility indicator for callers that specifically observe local budget rejection.</summary>
    public bool BudgetExhausted => Status == TileRetrievalStatus.BudgetRejected;

    /// <summary>Tile data retrieved successfully.</summary>
    public static TileRetrievalResult Success(byte[] data) =>
        new(TileRetrievalStatus.Success, data);

    /// <summary>Tile absence confirmed locally or by an upstream 404 response.</summary>
    public static TileRetrievalResult NotFound() =>
        new(TileRetrievalStatus.NotFound);

    /// <summary>Permanent provider rejection that must not be retried.</summary>
    public static TileRetrievalResult PermanentFailure() =>
        new(TileRetrievalStatus.PermanentFailure);

    /// <summary>Bounded provider retries exhausted or were deferred by a provider gate.</summary>
    public static TileRetrievalResult TransientFailure(int retryAfterSeconds) =>
        new(TileRetrievalStatus.TransientFailure, RetryAfterSeconds: retryAfterSeconds);

    /// <summary>Local outbound allowance or global capacity rejected the request.</summary>
    public static TileRetrievalResult Throttled(int retryAfterSeconds) =>
        new(TileRetrievalStatus.BudgetRejected, RetryAfterSeconds: retryAfterSeconds);
}

/// <summary>Internal typed outcome of one cold-cache fill series.</summary>
internal enum TileCacheFillStatus
{
    Cached,
    NotFound,
    PermanentFailure,
    TransientFailure,
    BudgetRejected
}

/// <summary>Internal cache-fill outcome and its unbounded provider delay evidence.</summary>
internal readonly record struct TileCacheFillResult(
    TileCacheFillStatus Status,
    TimeSpan RetryAfter)
{
    internal static TileCacheFillResult Cached() =>
        new(TileCacheFillStatus.Cached, TimeSpan.Zero);

    internal static TileCacheFillResult NotFound() =>
        new(TileCacheFillStatus.NotFound, TimeSpan.Zero);

    internal static TileCacheFillResult PermanentFailure() =>
        new(TileCacheFillStatus.PermanentFailure, TimeSpan.Zero);

    internal static TileCacheFillResult Transient(TimeSpan retryAfter) =>
        new(TileCacheFillStatus.TransientFailure, retryAfter);

    internal static TileCacheFillResult BudgetRejected() =>
        new(TileCacheFillStatus.BudgetRejected, TimeSpan.Zero);
}

/// <summary>Typed transport result passed to cache persistence after a retry series.</summary>
internal sealed record TileDownloadResult(
    TileCacheFillStatus Status,
    byte[]? TileData = null,
    string? ETag = null,
    DateTime? LastModifiedUpstream = null,
    DateTime? ExpiresAtUtc = null,
    TimeSpan RetryAfter = default)
{
    internal static TileDownloadResult Downloaded(
        byte[] tileData,
        string? etag,
        DateTime? lastModifiedUpstream,
        DateTime? expiresAtUtc) =>
        new(
            TileCacheFillStatus.Cached,
            tileData,
            etag,
            lastModifiedUpstream,
            expiresAtUtc);

    internal static TileDownloadResult NotFound() =>
        new(TileCacheFillStatus.NotFound);

    internal static TileDownloadResult PermanentFailure() =>
        new(TileCacheFillStatus.PermanentFailure);

    internal static TileDownloadResult Transient(TimeSpan retryAfter) =>
        new(TileCacheFillStatus.TransientFailure, RetryAfter: retryAfter);

    internal static TileDownloadResult BudgetRejected() =>
        new(TileCacheFillStatus.BudgetRejected);
}

/// <summary>Classifies a request that was rejected before receiving a usable provider response.</summary>
internal enum TileRequestRejection
{
    None,
    ClientBudget,
    GlobalBudget,
    ProviderDeferred,
    ContactLimit,
    InvalidProviderResponse
}

/// <summary>Captures either one owned provider response or a bounded pre-transport rejection.</summary>
internal sealed record TileRequestSendResult(
    HttpResponseMessage? Response,
    TileRequestRejection Rejection,
    TimeSpan RetryAfter)
{
    internal static TileRequestSendResult Succeeded(HttpResponseMessage response) =>
        new(response, TileRequestRejection.None, TimeSpan.Zero);

    internal static TileRequestSendResult Rejected(TileRequestRejection rejection) =>
        new(null, rejection, TimeSpan.Zero);

    internal static TileRequestSendResult ProviderDeferred(TimeSpan retryAfter) =>
        new(null, TileRequestRejection.ProviderDeferred, retryAfter);
}

/// <summary>Tracks actual provider contacts across every retry and redirect in one tile operation.</summary>
internal sealed class TileContactState
{
    private int _contacts;

    /// <summary>Indicates whether the interim operation-wide contact ceiling is exhausted.</summary>
    internal bool IsExhausted => Volatile.Read(ref _contacts) >= TileProviderRetryPolicy.MaxAttempts;

    /// <summary>Atomically reserves one actual provider contact without allowing a fourth.</summary>
    internal bool TryReserveContact()
    {
        while (true)
        {
            var current = Volatile.Read(ref _contacts);
            if (current >= TileProviderRetryPolicy.MaxAttempts)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _contacts, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>Classifies one stale revalidation attempt without conflating it with cold-cache retrieval.</summary>
internal enum StaleRefreshOutcome
{
    Completed,
    Terminal,
    Transient,
    PreTransportRejected
}
