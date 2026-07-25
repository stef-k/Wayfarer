using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services;

public partial class TileCacheService
{
    /// <summary>Stops all tile admission and waits only within the application shutdown bound.</summary>
    private static void StopAndDrainTileWork()
    {
        OutboundBudget.Stop();
        StopAndDrainTileWorkAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private static async Task StopAndDrainTileWorkAsync(TimeSpan timeout)
    {
        var schedulerDrain = TileWorkScheduler.StopAndDrainAsync();
        var refreshDrain = StopAndWaitForRefreshesAsync(timeout);
        try
        {
            await Task.WhenAll(schedulerDrain, refreshDrain)
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Unfinished state remains owned until process exit; shutdown itself stays bounded.
        }
    }

    /// <summary>
    /// Coalesces bounded background refresh series for expired cached tiles.
    /// Key: "{provider fingerprint}:{z}_{x}_{y}". At most one active series may exist per tile key.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TileRefreshSeries> _refreshSeries = new();
    private static readonly object _refreshAdmissionLock = new();
    private static bool _refreshStopping;

    /// <summary>
    /// Maximum number of upstream attempts in one stale-tile refresh series.
    /// </summary>
    /// <summary>
    /// Test-overridable delay provider for bounded refresh retry backoff.
    /// </summary>
    private static Func<int, TimeSpan> _refreshRetryDelayProvider = CalculateRefreshRetryDelay;

    /// <summary>
    /// Test-overridable tile replacement hook for deterministic replacement-failure coverage.
    /// </summary>
    private static Action<string, string> _replaceTileFile = ReplaceTileFileAtomicallyCore;

    /// <summary>
    /// Schedules a bounded background refresh for an expired local tile.
    /// Concurrent stale hits for the same key share the active series.
    /// </summary>
    private void ScheduleBackgroundRefresh(
        string tileUrl,
        string tileFilePath,
        string tileKey,
        string providerIdentity,
        int zoom, int x, int y, string? etag, DateTime? lastModified, string? clientIp,
        string? publicOrigin)
    {
        TileRefreshSeries series;
        lock (_refreshAdmissionLock)
        {
            if (_refreshStopping)
            {
                TileCacheDiagnostics.StaleRefreshRejected(_logger, "shutdown", zoom);
                return;
            }

            if (_refreshSeries.ContainsKey(tileKey))
            {
                TileCacheDiagnostics.StaleRefreshCoalesced(_logger, "coalesced", zoom);
                _logger.LogDebug("Refresh already active for stale tile {TileKey}", tileKey);
                return;
            }

            if (_refreshSeries.Count >= TileWorkScheduler.BackgroundQueueCapacity)
            {
                TileCacheDiagnostics.StaleRefreshRejected(_logger, "background-queue-full", zoom);
                return;
            }

            series = new TileRefreshSeries(
                tileKey, providerIdentity, tileUrl, tileFilePath,
                zoom, x, y, etag, lastModified, clientIp, publicOrigin,
                TileProviderPolicyResolver.Resolve(_applicationSettings.GetSettings(), _logger));
            _refreshSeries[tileKey] = series;
            series.SetCompletion(Task.Run(
                () => RunBackgroundRefreshSeriesAsync(series),
                CancellationToken.None));
        }

        TileCacheDiagnostics.StaleRefreshScheduled(_logger, "scheduled", zoom);
    }

    /// <summary>
    /// Runs one bounded refresh series with exponential jittered backoff.
    /// </summary>
    private async Task RunBackgroundRefreshSeriesAsync(TileRefreshSeries series)
    {
        try
        {
            while (series.Attempts < series.ProviderPolicy.MaxAttempts &&
                   TileProviderRetryPolicy.UtcNow < series.DeadlineUtc)
            {
                series.Attempts++;
                series.CancellationStage = "stale-refresh-attempt";

                try
                {
                    var outcome = await RevalidateTileInFreshScopeAsync(series);
                    if (outcome is StaleRefreshOutcome.Completed or StaleRefreshOutcome.Terminal ||
                        series.ContactState.IsExhausted)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The outer boundary owns cancellation classification and deterministic state removal.
                    throw;
                }
                catch (Exception)
                {
                    _logger.LogWarning("Background refresh attempt {Attempt} failed for tile {TileKey}",
                        series.Attempts, series.TileKey);
                }

                if (series.Attempts >= series.ProviderPolicy.MaxAttempts)
                {
                    break;
                }

                var remaining = series.DeadlineUtc - TileProviderRetryPolicy.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = _refreshRetryDelayProvider == CalculateRefreshRetryDelay
                    ? TileProviderRetryPolicy.GetFallbackDelay(series.Attempts, series.ProviderPolicy)
                    : _refreshRetryDelayProvider(series.Attempts);
                var selectedDelay = delay > remaining ? remaining : delay;
                TileCacheDiagnostics.RetryDelaySelected(
                    _logger,
                    selectedDelay.TotalMilliseconds,
                    "stale-refresh");
                series.CancellationStage = "stale-refresh-delay";
                await Task.Delay(selectedDelay, series.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (series.CancellationToken.IsCancellationRequested)
        {
            TileCacheDiagnostics.Cancellation(_logger, series.CancellationStage);
            _logger.LogDebug("Background refresh cancelled for tile {TileKey}", series.TileKey);
        }
        catch (OperationCanceledException)
        {
            // The transport already emitted a privacy-safe upstream-failure diagnostic.
        }
        finally
        {
            lock (_refreshAdmissionLock)
            {
                _refreshSeries.TryRemove(
                    new KeyValuePair<string, TileRefreshSeries>(series.TileKey, series));
            }
        }
    }

    /// <summary>
    /// Uses the shared interim exponential retry delay with injectable jitter.
    /// </summary>
    private static TimeSpan CalculateRefreshRetryDelay(int failedAttempts) =>
        TileProviderRetryPolicy.GetFallbackDelay(failedAttempts);

    /// <summary>
    /// Runs a background refresh attempt through a newly-created DI scope.
    /// The scheduled series carries only immutable primitive values from the request.
    /// </summary>
    private async Task<StaleRefreshOutcome> RevalidateTileInFreshScopeAsync(TileRefreshSeries series)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        return await tileCacheService.RevalidateTileAsync(series);
    }

    /// <summary>
    /// Creates a same-directory temporary path for atomic tile replacement.
    /// </summary>
    private static string CreateTempTilePath(string tileFilePath)
    {
        var directory = Path.GetDirectoryName(tileFilePath) ?? ".";
        var fileName = Path.GetFileName(tileFilePath);
        return Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Replaces the final tile with a same-directory temp file so readers never see partial bytes.
    /// </summary>
    private static void ReplaceTileFileAtomically(string tempFilePath, string tileFilePath) =>
        _replaceTileFile(tempFilePath, tileFilePath);

    /// <summary>
    /// Replaces the final tile with a same-directory temp file using the production file operation.
    /// </summary>
    private static void ReplaceTileFileAtomicallyCore(string tempFilePath, string tileFilePath)
    {
        if (File.Exists(tileFilePath))
        {
            File.Replace(tempFilePath, tileFilePath, null);
            return;
        }

        File.Move(tempFilePath, tileFilePath);
    }

    /// <summary>
    /// Deletes a failed refresh temp file without masking the original replacement error.
    /// </summary>
    private static void TryDeleteTempTile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    /// <summary>
    /// Waits until a tile refresh series is no longer active.
    /// Test-only helper for observing fire-and-forget background refresh completion.
    /// </summary>
    internal static async Task<bool> WaitForRefreshIdleForTestingAsync(string tileKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (!ContainsRefreshKey(tileKey))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return !ContainsRefreshKey(tileKey);
    }

    /// <summary>
    /// Cancels an active tile refresh series in tests.
    /// </summary>
    internal static void CancelRefreshForTesting(string tileKey)
    {
        var series = _refreshSeries
            .FirstOrDefault(entry => MatchesRefreshKey(entry.Key, tileKey))
            .Value;
        if (series != null)
        {
            series.CancelForTesting();
        }
    }

    /// <summary>Matches current provider-scoped keys and legacy coordinate-only test keys.</summary>
    private static bool ContainsRefreshKey(string tileKey) =>
        _refreshSeries.Keys.Any(key => MatchesRefreshKey(key, tileKey));

    private static bool MatchesRefreshKey(string candidate, string requested) =>
        candidate.Equals(requested, StringComparison.Ordinal) ||
        candidate.EndsWith($":{requested}", StringComparison.Ordinal);

    /// <summary>Cancels and boundedly awaits all refresh work tracked by the test process.</summary>
    internal static async Task<bool> CancelAndWaitForRefreshesForTestingAsync(TimeSpan timeout)
        => await CancelAndWaitForRefreshesAsync(timeout, stopAdmission: false).ConfigureAwait(false);

    /// <summary>Stops new refresh admission and boundedly drains all accepted refresh series.</summary>
    internal static async Task<bool> StopAndWaitForRefreshesAsync(TimeSpan timeout)
        => await CancelAndWaitForRefreshesAsync(timeout, stopAdmission: true).ConfigureAwait(false);

    private static async Task<bool> CancelAndWaitForRefreshesAsync(
        TimeSpan timeout,
        bool stopAdmission)
    {
        TileRefreshSeries[] activeSeries;
        lock (_refreshAdmissionLock)
        {
            if (stopAdmission)
            {
                _refreshStopping = true;
            }

            activeSeries = _refreshSeries.Values.ToArray();
        }

        foreach (var series in activeSeries)
        {
            series.CancelForTesting();
        }

        try
        {
            await Task.WhenAll(activeSeries.Select(series => series.Completion))
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        return activeSeries.All(series => !_refreshSeries.ContainsKey(series.TileKey));
    }

    /// <summary>
    /// Overrides refresh retry delay calculation for deterministic tests.
    /// </summary>
    internal static void SetRefreshRetryDelayForTesting(Func<int, TimeSpan>? delayProvider)
    {
        _refreshRetryDelayProvider = delayProvider ?? CalculateRefreshRetryDelay;
    }

    /// <summary>
    /// Overrides tile replacement for deterministic replacement-failure tests.
    /// </summary>
    internal static void SetTileFileReplacerForTesting(Action<string, string>? replacer)
    {
        _replaceTileFile = replacer ?? ReplaceTileFileAtomicallyCore;
    }

    /// <summary>Reopens background admission after isolated test cleanup.</summary>
    private static void ResetRefreshCoordinatorForTesting()
    {
        lock (_refreshAdmissionLock)
        {
            _refreshStopping = false;
        }
    }

    /// <summary>
    /// Captures immutable inputs and retry state for one bounded stale-tile refresh series.
    /// </summary>
    private sealed class TileRefreshSeries
    {
        public string TileKey { get; }
        public string ProviderIdentity { get; }
        public string TileUrl { get; }
        public string TileFilePath { get; }
        public int Zoom { get; }
        public int X { get; }
        public int Y { get; }
        public string? ETag { get; }
        public DateTime? LastModified { get; }
        public string? ClientIp { get; }
        public string? PublicOrigin { get; }
        public TileProviderPolicy ProviderPolicy { get; }
        public DateTimeOffset DeadlineUtc { get; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public int Attempts { get; set; }

        /// <summary>Gets shared actual-contact state for all redirects and retries in this series.</summary>
        public TileContactState ContactState { get; } = new();

        /// <summary>Gets or sets whether the initiating client's outbound allowance was recorded.</summary>
        public bool ClientAllowanceCharged { get; set; }

        /// <summary>Gets or sets the bounded stage owned by the outer cancellation boundary.</summary>
        public string CancellationStage { get; set; } = "stale-refresh-attempt";

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>Gets the scheduled series task so test cleanup can await its completion.</summary>
        public Task Completion { get; private set; } = Task.CompletedTask;

        public TileRefreshSeries(string tileKey, string providerIdentity, string tileUrl, string tileFilePath,
            int zoom, int x, int y, string? etag, DateTime? lastModified, string? clientIp,
            string? publicOrigin, TileProviderPolicy providerPolicy)
        {
            TileKey = tileKey;
            ProviderIdentity = providerIdentity;
            TileUrl = tileUrl;
            TileFilePath = tileFilePath;
            Zoom = zoom;
            X = x;
            Y = y;
            ETag = etag;
            LastModified = lastModified;
            ClientIp = clientIp;
            PublicOrigin = publicOrigin;
            ProviderPolicy = providerPolicy;
            DeadlineUtc = TileProviderRetryPolicy.UtcNow + providerPolicy.TotalRetryCeiling;
        }

        public void CancelForTesting() => _cancellationTokenSource.Cancel();

        /// <summary>Records the single scheduled task for this series.</summary>
        public void SetCompletion(Task completion) => Completion = completion;
    }
}
