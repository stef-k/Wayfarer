using System.Collections.Concurrent;

public partial class TileCacheService
{
    /// <summary>
    /// Coalesces bounded background refresh series for expired cached tiles.
    /// Key: "{z}_{x}_{y}". At most one active series may exist per tile key.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TileRefreshSeries> _refreshSeries = new();

    /// <summary>
    /// Maximum number of upstream attempts in one stale-tile refresh series.
    /// </summary>
    private const int RefreshSeriesMaxAttempts = 3;

    /// <summary>
    /// Maximum wall-clock lifetime for one stale-tile refresh series.
    /// </summary>
    private static readonly TimeSpan RefreshSeriesMaxDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Initial delay before retrying a failed stale-tile refresh attempt.
    /// </summary>
    private static readonly TimeSpan RefreshRetryInitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum delay before retrying a failed stale-tile refresh attempt.
    /// </summary>
    private static readonly TimeSpan RefreshRetryMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Schedules a bounded background refresh for an expired local tile.
    /// Concurrent stale hits for the same key share the active series.
    /// </summary>
    private void ScheduleBackgroundRefresh(string tileUrl, string tileFilePath, string tileKey,
        int zoom, int x, int y, string? etag, DateTime? lastModified, string? clientIp)
    {
        var series = new TileRefreshSeries(tileKey, tileUrl, tileFilePath, zoom, x, y, etag, lastModified, clientIp);
        var activeSeries = _refreshSeries.GetOrAdd(tileKey, series);
        if (!ReferenceEquals(activeSeries, series))
        {
            _logger.LogDebug("Refresh already active for stale tile {TileKey}", tileKey);
            return;
        }

        _ = Task.Run(() => RunBackgroundRefreshSeriesAsync(series), CancellationToken.None);
    }

    /// <summary>
    /// Runs one bounded refresh series with exponential jittered backoff.
    /// </summary>
    private async Task RunBackgroundRefreshSeriesAsync(TileRefreshSeries series)
    {
        try
        {
            while (series.Attempts < RefreshSeriesMaxAttempts &&
                   DateTime.UtcNow - series.StartedAtUtc < RefreshSeriesMaxDuration)
            {
                series.Attempts++;

                try
                {
                    var refreshed = await RevalidateTileAsync(series.TileUrl, series.TileFilePath,
                        series.TileKey, series.Zoom, series.X, series.Y, series.ETag,
                        series.LastModified, series.ClientIp, series.CancellationToken);

                    if (refreshed != null)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background refresh attempt {Attempt} failed for tile {TileKey}",
                        series.Attempts, series.TileKey);
                }

                if (series.Attempts >= RefreshSeriesMaxAttempts)
                {
                    break;
                }

                var remaining = RefreshSeriesMaxDuration - (DateTime.UtcNow - series.StartedAtUtc);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = CalculateRefreshRetryDelay(series.Attempts);
                await Task.Delay(delay > remaining ? remaining : delay, series.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background refresh cancelled for tile {TileKey}", series.TileKey);
        }
        finally
        {
            _refreshSeries.TryRemove(new KeyValuePair<string, TileRefreshSeries>(series.TileKey, series));
        }
    }

    /// <summary>
    /// Calculates exponential refresh retry delay with jitter to avoid synchronized retries.
    /// </summary>
    private static TimeSpan CalculateRefreshRetryDelay(int failedAttempts)
    {
        var exponent = Math.Max(0, failedAttempts - 1);
        var delayMs = RefreshRetryInitialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        delayMs = Math.Min(delayMs, RefreshRetryMaxDelay.TotalMilliseconds);
        delayMs *= 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(delayMs);
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
    private static void ReplaceTileFileAtomically(string tempFilePath, string tileFilePath)
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
            if (!_refreshSeries.ContainsKey(tileKey))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return !_refreshSeries.ContainsKey(tileKey);
    }

    /// <summary>
    /// Cancels an active tile refresh series in tests.
    /// </summary>
    internal static void CancelRefreshForTesting(string tileKey)
    {
        if (_refreshSeries.TryGetValue(tileKey, out var series))
        {
            series.CancelForTesting();
        }
    }

    /// <summary>
    /// Captures immutable inputs and retry state for one bounded stale-tile refresh series.
    /// </summary>
    private sealed class TileRefreshSeries
    {
        public string TileKey { get; }
        public string TileUrl { get; }
        public string TileFilePath { get; }
        public int Zoom { get; }
        public int X { get; }
        public int Y { get; }
        public string? ETag { get; }
        public DateTime? LastModified { get; }
        public string? ClientIp { get; }
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public int Attempts { get; set; }

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public TileRefreshSeries(string tileKey, string tileUrl, string tileFilePath,
            int zoom, int x, int y, string? etag, DateTime? lastModified, string? clientIp)
        {
            TileKey = tileKey;
            TileUrl = tileUrl;
            TileFilePath = tileFilePath;
            Zoom = zoom;
            X = x;
            Y = y;
            ETag = etag;
            LastModified = lastModified;
            ClientIp = clientIp;
        }

        public void CancelForTesting() => _cancellationTokenSource.Cancel();
    }
}
