using Microsoft.Extensions.Logging;

namespace Wayfarer.Services;

/// <summary>
/// Stable identifiers for structured tile-pipeline diagnostics.
/// </summary>
internal enum TileCacheDiagnosticEventIds
{
    /// <summary>A complete, unexpired tile was served locally.</summary>
    FreshCacheHit = 38500,

    /// <summary>An expired tile was served locally while refresh proceeded independently.</summary>
    StaleCacheHit = 38501,

    /// <summary>A new stale refresh series was scheduled.</summary>
    StaleRefreshScheduled = 38502,

    /// <summary>A stale hit joined an already-active refresh series.</summary>
    StaleRefreshCoalesced = 38503,

    /// <summary>A stale refresh could not make an upstream request.</summary>
    StaleRefreshRejected = 38504,

    /// <summary>A requested tile was absent from the local cache.</summary>
    ColdCacheMiss = 38505,

    /// <summary>The global outbound budget rejected an acquisition.</summary>
    GlobalBudgetRejected = 38506,

    /// <summary>The client outbound allowance rejected an acquisition.</summary>
    ClientBudgetRejected = 38507,

    /// <summary>An upstream HTTP request was attempted.</summary>
    UpstreamAttempt = 38508,

    /// <summary>An upstream HTTP status was received.</summary>
    UpstreamStatus = 38509,

    /// <summary>The current retry policy selected a delay.</summary>
    RetryDelaySelected = 38510,

    /// <summary>A global budget wait completed.</summary>
    BudgetWait = 38511,

    /// <summary>A pipeline operation was cancelled.</summary>
    Cancellation = 38512,

    /// <summary>A local cache-write operation completed.</summary>
    CacheWriteOutcome = 38513,

    /// <summary>A conditional upstream response was processed.</summary>
    ConditionalResponseOutcome = 38514,

    /// <summary>An upstream operation failed without an HTTP response.</summary>
    UpstreamFailure = 38515,

    /// <summary>A provider-wide delay gate was established, awaited, or rejected.</summary>
    ProviderDelay = 38516,

    /// <summary>An upstream outcome was classified for controller-safe handling.</summary>
    UpstreamClassification = 38517
}

/// <summary>
/// Result of one global outbound-budget acquisition, including deterministic wait evidence.
/// </summary>
/// <param name="Acquired">Whether the request acquired outbound capacity.</param>
/// <param name="WaitDuration">How long acquisition waited before completing.</param>
internal readonly record struct OutboundBudgetAcquisition(bool Acquired, TimeSpan WaitDuration);

/// <summary>
/// Emits privacy-safe structured events without coupling diagnostics to scheduling policy.
/// </summary>
internal static partial class TileCacheDiagnostics
{
    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.FreshCacheHit,
        EventName = nameof(TileCacheDiagnosticEventIds.FreshCacheHit),
        Level = LogLevel.Debug,
        Message = "Tile cache lookup completed with {CacheOutcome} outcome at zoom {Zoom}.")]
    public static partial void FreshCacheHit(ILogger logger, string cacheOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.StaleCacheHit,
        EventName = nameof(TileCacheDiagnosticEventIds.StaleCacheHit),
        Level = LogLevel.Debug,
        Message = "Tile cache lookup completed with {CacheOutcome} outcome at zoom {Zoom}.")]
    public static partial void StaleCacheHit(ILogger logger, string cacheOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.StaleRefreshScheduled,
        EventName = nameof(TileCacheDiagnosticEventIds.StaleRefreshScheduled),
        Level = LogLevel.Debug,
        Message = "Stale tile refresh was {RefreshOutcome} at zoom {Zoom}.")]
    public static partial void StaleRefreshScheduled(ILogger logger, string refreshOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.StaleRefreshCoalesced,
        EventName = nameof(TileCacheDiagnosticEventIds.StaleRefreshCoalesced),
        Level = LogLevel.Debug,
        Message = "Stale tile refresh was {RefreshOutcome} at zoom {Zoom}.")]
    public static partial void StaleRefreshCoalesced(ILogger logger, string refreshOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.StaleRefreshRejected,
        EventName = nameof(TileCacheDiagnosticEventIds.StaleRefreshRejected),
        Level = LogLevel.Debug,
        Message = "Stale tile refresh was {RefreshOutcome} at zoom {Zoom}.")]
    public static partial void StaleRefreshRejected(ILogger logger, string refreshOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.ColdCacheMiss,
        EventName = nameof(TileCacheDiagnosticEventIds.ColdCacheMiss),
        Level = LogLevel.Debug,
        Message = "Tile cache lookup completed with {CacheOutcome} outcome at zoom {Zoom}.")]
    public static partial void ColdCacheMiss(ILogger logger, string cacheOutcome, int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.GlobalBudgetRejected,
        EventName = nameof(TileCacheDiagnosticEventIds.GlobalBudgetRejected),
        Level = LogLevel.Warning,
        Message = "{BudgetScope} outbound budget rejected tile work after {WaitMilliseconds} ms.")]
    public static partial void GlobalBudgetRejected(
        ILogger logger,
        string budgetScope,
        double waitMilliseconds);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.ClientBudgetRejected,
        EventName = nameof(TileCacheDiagnosticEventIds.ClientBudgetRejected),
        Level = LogLevel.Warning,
        Message = "{BudgetScope} outbound allowance rejected tile work.")]
    public static partial void ClientBudgetRejected(ILogger logger, string budgetScope);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.UpstreamAttempt,
        EventName = nameof(TileCacheDiagnosticEventIds.UpstreamAttempt),
        Level = LogLevel.Debug,
        Message = "Tile upstream {RequestKind} request attempt {AttemptNumber} started.")]
    public static partial void UpstreamAttempt(
        ILogger logger,
        string requestKind,
        int attemptNumber);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.UpstreamStatus,
        EventName = nameof(TileCacheDiagnosticEventIds.UpstreamStatus),
        Level = LogLevel.Debug,
        Message = "Tile upstream {RequestKind} request attempt {AttemptNumber} returned status {StatusCode}.")]
    public static partial void UpstreamStatus(
        ILogger logger,
        string requestKind,
        int attemptNumber,
        int statusCode);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.RetryDelaySelected,
        EventName = nameof(TileCacheDiagnosticEventIds.RetryDelaySelected),
        Level = LogLevel.Debug,
        Message = "Tile retry delay selected as {RetryDelayMilliseconds} ms for {RetryKind}.")]
    public static partial void RetryDelaySelected(
        ILogger logger,
        double retryDelayMilliseconds,
        string retryKind);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.BudgetWait,
        EventName = nameof(TileCacheDiagnosticEventIds.BudgetWait),
        Level = LogLevel.Debug,
        Message = "Global outbound budget wait completed with {BudgetOutcome} after {WaitMilliseconds} ms.")]
    public static partial void BudgetWait(
        ILogger logger,
        string budgetOutcome,
        double waitMilliseconds);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.Cancellation,
        EventName = nameof(TileCacheDiagnosticEventIds.Cancellation),
        Level = LogLevel.Debug,
        Message = "Tile pipeline was cancelled during {CancellationStage}.")]
    public static partial void Cancellation(ILogger logger, string cancellationStage);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.CacheWriteOutcome,
        EventName = nameof(TileCacheDiagnosticEventIds.CacheWriteOutcome),
        Level = LogLevel.Debug,
        Message = "Tile cache write completed with {CacheWriteOutcome} outcome at zoom {Zoom}.")]
    public static partial void CacheWriteOutcome(
        ILogger logger,
        string cacheWriteOutcome,
        int zoom);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.ConditionalResponseOutcome,
        EventName = nameof(TileCacheDiagnosticEventIds.ConditionalResponseOutcome),
        Level = LogLevel.Debug,
        Message = "Conditional tile response completed with {ConditionalOutcome} outcome and status {StatusCode}.")]
    public static partial void ConditionalResponseOutcome(
        ILogger logger,
        string conditionalOutcome,
        int statusCode);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.UpstreamFailure,
        EventName = nameof(TileCacheDiagnosticEventIds.UpstreamFailure),
        Level = LogLevel.Warning,
        Message = "Tile upstream operation failed during {FailureStage}.")]
    public static partial void UpstreamFailure(
        ILogger logger,
        string failureStage);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.ProviderDelay,
        EventName = nameof(TileCacheDiagnosticEventIds.ProviderDelay),
        Level = LogLevel.Warning,
        Message = "Tile provider delay decision was {ProviderDelayOutcome} with {DelayMilliseconds} ms remaining.")]
    public static partial void ProviderDelay(
        ILogger logger,
        string providerDelayOutcome,
        double delayMilliseconds);

    [LoggerMessage(
        EventId = (int)TileCacheDiagnosticEventIds.UpstreamClassification,
        EventName = nameof(TileCacheDiagnosticEventIds.UpstreamClassification),
        Level = LogLevel.Debug,
        Message = "Tile upstream outcome was classified as {UpstreamOutcome} at attempt {AttemptNumber}.")]
    public static partial void UpstreamClassification(
        ILogger logger,
        string upstreamOutcome,
        int attemptNumber);
}
