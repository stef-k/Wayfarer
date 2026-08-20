using System.Collections.Concurrent;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Fetches external images, optimizes them via ImageSharp, and stores them in the
/// proxied image disk cache. Delegates to <see cref="ImageProxyHelper"/> for
/// shared SSRF checks, cache key computation, and image optimization.
/// </summary>
public class ImageProxyService : IImageProxyService
{
    private readonly HttpClient _httpClient;
    private readonly IProxiedImageCacheService _imageCacheService;
    private readonly IApplicationSettingsService _settingsService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ImageProxyService> _logger;

    /// <summary>
    /// Coalesces active origin download and ImageSharp work by image cache key.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageProxyResult>>> _originWork = new();

    /// <summary>
    /// Coalesces bounded stale refresh series by image cache key.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ImageRefreshSeries> _refreshSeries = new();

    /// <summary>
    /// Process-wide concurrency budget for origin download and ImageSharp optimization.
    /// </summary>
    private static readonly SemaphoreSlim _originWorkBudget = new(4, 4);

    private const int RefreshSeriesMaxAttempts = 3;
    private static readonly TimeSpan RefreshSeriesMaxDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshRetryInitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshRetryMaxDelay = TimeSpan.FromSeconds(60);
    private static Func<int, TimeSpan> _refreshRetryDelayProvider = CalculateRefreshRetryDelay;
    private static TimeSpan _refreshSeriesMaxDuration = RefreshSeriesMaxDuration;

    public ImageProxyService(
        HttpClient httpClient,
        IProxiedImageCacheService imageCacheService,
        IApplicationSettingsService settingsService,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ImageProxyService> logger)
    {
        _httpClient = httpClient;
        _imageCacheService = imageCacheService;
        _settingsService = settingsService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ImageProxyResult> GetOrFetchAsync(
        ImageProxyRequest request,
        bool allowOriginFetch,
        CancellationToken ct = default)
    {
        if (!ImageProxyHelper.IsUrlAllowed(request.Url))
        {
            _logger.LogDebug("Image URL disallowed by SSRF check: {Url}", request.Url);
            return new ImageProxyResult(ImageProxyResultStatus.BadRequest, string.Empty, null, null);
        }

        var cacheKey = ComputeCacheKey(request);
        var cached = await _imageCacheService.GetAsync(cacheKey);
        if (cached.Status is ProxiedImageCacheStatus.FreshHit or ProxiedImageCacheStatus.StaleHit && cached.HasBytes)
        {
            if (cached.Status == ProxiedImageCacheStatus.StaleHit)
            {
                ScheduleBackgroundRefresh(cacheKey, request);
            }

            var status = cached.Status == ProxiedImageCacheStatus.FreshHit
                ? ImageProxyResultStatus.FreshHit
                : ImageProxyResultStatus.StaleHit;
            return new ImageProxyResult(status, cacheKey, cached.Bytes, cached.ContentType);
        }

        if (!allowOriginFetch)
        {
            return new ImageProxyResult(ImageProxyResultStatus.OriginRequired, cacheKey, null, null);
        }

        return await RunOriginWorkCoalescedAsync(
            cacheKey,
            () => DownloadOptimizeAndCacheAsync(request, cacheKey, ct),
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> FetchAndCacheAsync(string imageUrl, CancellationToken ct = default)
    {
        var request = new ImageProxyRequest(imageUrl);
        if (!ImageProxyHelper.IsUrlAllowed(imageUrl))
            return false;

        var cacheKey = ComputeCacheKey(request);
        var existing = await _imageCacheService.GetAsync(cacheKey);
        if (existing.Status == ProxiedImageCacheStatus.FreshHit)
        {
            return false;
        }

        var result = existing.Status == ProxiedImageCacheStatus.StaleHit
            ? await RefreshAsync(request, ct)
            : await RunOriginWorkCoalescedAsync(
                cacheKey,
                () => DownloadOptimizeAndCacheAsync(request, cacheKey, ct),
                ct);

        return result.Status == ImageProxyResultStatus.Fetched;
    }

    /// <inheritdoc />
    public Task<ImageProxyResult> RefreshAsync(ImageProxyRequest request, CancellationToken ct = default)
    {
        if (!ImageProxyHelper.IsUrlAllowed(request.Url))
        {
            return Task.FromResult(new ImageProxyResult(ImageProxyResultStatus.BadRequest, string.Empty, null, null));
        }

        var cacheKey = ComputeCacheKey(request);
        return RunOriginWorkCoalescedAsync(
            cacheKey,
            () => DownloadOptimizeAndCacheAsync(request, cacheKey, ct),
            ct);
    }

    /// <summary>
    /// Runs origin work once per cache key and shares the result with concurrent callers.
    /// </summary>
    private static async Task<ImageProxyResult> RunOriginWorkCoalescedAsync(
        string cacheKey,
        Func<Task<ImageProxyResult>> work,
        CancellationToken ct)
    {
        var lazy = new Lazy<Task<ImageProxyResult>>(async () =>
        {
            await _originWorkBudget.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                _originWorkBudget.Release();
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        var active = _originWork.GetOrAdd(cacheKey, lazy);
        try
        {
            return await active.Value.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(active, lazy))
            {
                _originWork.TryRemove(new KeyValuePair<string, Lazy<Task<ImageProxyResult>>>(cacheKey, lazy));
            }
        }
    }

    /// <summary>
    /// Downloads, optionally optimizes, and stores an image for one cache key.
    /// </summary>
    private async Task<ImageProxyResult> DownloadOptimizeAndCacheAsync(
        ImageProxyRequest request,
        string cacheKey,
        CancellationToken ct)
    {
        var maxBytes = _settingsService.GetSettings().MaxProxyImageDownloadMB * 1024L * 1024;
        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download image from {Url}.", request.Url);
            return new ImageProxyResult(ImageProxyResultStatus.Failed, cacheKey, null, null);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Upstream returned {StatusCode} for image {Url}.", (int)resp.StatusCode, request.Url);
                return new ImageProxyResult(ImageProxyResultStatus.NotFound, cacheKey, null, null);
            }

            if (resp.Content.Headers.ContentLength > maxBytes)
            {
                _logger.LogWarning("Image too large ({Size} bytes) from {Url}.", resp.Content.Headers.ContentLength, request.Url);
                return new ImageProxyResult(ImageProxyResultStatus.TooLarge, cacheKey, null, null);
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var bytes = await ReadWithLimitAsync(resp, maxBytes, ct);
            if (bytes == null)
            {
                return new ImageProxyResult(ImageProxyResultStatus.TooLarge, cacheKey, null, null);
            }

            if (request.Optimize && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bytes = ImageProxyHelper.OptimizeImage(
                        bytes,
                        request.MaxWidth,
                        request.MaxHeight,
                        request.Quality ?? 95,
                        out var isPng);
                    contentType = isPng ? "image/png" : "image/jpeg";
                }
                catch (DecodedImageResourceRejectedException ex)
                {
                    _logger.LogInformation(
                        "Rejected image proxy cache key {CacheKey}: {LimitName} observed {Observed}, limit {Limit}.",
                        cacheKey,
                        ex.Result.LimitName,
                        ex.Result.Observed,
                        ex.Result.Limit);
                    return new ImageProxyResult(ImageProxyResultStatus.TooLarge, cacheKey, null, null);
                }
                catch (Exception)
                {
                    _logger.LogDebug("Failed to optimize image for cache key {CacheKey}.", cacheKey);
                    return new ImageProxyResult(ImageProxyResultStatus.Failed, cacheKey, null, null);
                }
            }

            var stored = await _imageCacheService.SetAsync(cacheKey, bytes, contentType);
            if (stored?.Stored != true)
            {
                _logger.LogWarning("Failed to store proxied image cache entry for {Url}.", request.Url);
                return new ImageProxyResult(ImageProxyResultStatus.Failed, cacheKey, null, null);
            }

            _logger.LogDebug("Cached proxied image: {Url} ({Size} bytes).", request.Url, bytes.Length);
            return new ImageProxyResult(ImageProxyResultStatus.Fetched, cacheKey, bytes, contentType);
        }
    }

    /// <summary>
    /// Reads an origin response with a hard maximum byte count.
    /// </summary>
    private static async Task<byte[]?> ReadWithLimitAsync(HttpResponseMessage resp, long maxBytes, CancellationToken ct)
    {
        await using var bodyStream = await resp.Content.ReadAsStreamAsync(ct);
        using var limitedStream = new MemoryStream();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await bodyStream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                return null;
            }

            limitedStream.Write(buffer, 0, read);
        }

        return limitedStream.ToArray();
    }

    /// <summary>
    /// Schedules a bounded stale refresh series if one is not already active for the key.
    /// </summary>
    private void ScheduleBackgroundRefresh(string cacheKey, ImageProxyRequest request)
    {
        var series = new ImageRefreshSeries(cacheKey, request);
        var activeSeries = _refreshSeries.GetOrAdd(cacheKey, series);
        if (!ReferenceEquals(activeSeries, series))
        {
            _logger.LogDebug("Image refresh already active for cache key {CacheKey}.", cacheKey);
            return;
        }

        _ = Task.Run(() => RunBackgroundRefreshSeriesAsync(series), CancellationToken.None);
    }

    /// <summary>
    /// Runs one bounded background refresh series through fresh DI scopes.
    /// </summary>
    private async Task RunBackgroundRefreshSeriesAsync(ImageRefreshSeries series)
    {
        try
        {
            while (series.Attempts < RefreshSeriesMaxAttempts &&
                   DateTime.UtcNow < series.ExpiresAtUtc)
            {
                series.Attempts++;

                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var imageProxyService = scope.ServiceProvider.GetRequiredService<IImageProxyService>();
                    var result = await imageProxyService.RefreshAsync(series.Request, series.CancellationToken);
                    if (result.Status == ImageProxyResultStatus.Fetched)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background image refresh attempt {Attempt} failed for key {CacheKey}.",
                        series.Attempts, series.CacheKey);
                }

                if (series.Attempts >= RefreshSeriesMaxAttempts)
                {
                    break;
                }

                var remaining = series.ExpiresAtUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = _refreshRetryDelayProvider(series.Attempts);
                await Task.Delay(delay > remaining ? remaining : delay, series.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background image refresh cancelled for key {CacheKey}.", series.CacheKey);
        }
        finally
        {
            _refreshSeries.TryRemove(new KeyValuePair<string, ImageRefreshSeries>(series.CacheKey, series));
        }
    }

    /// <summary>
    /// Calculates exponential refresh retry delay with jitter.
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
    /// Computes the deterministic cache key for a request.
    /// </summary>
    private static string ComputeCacheKey(ImageProxyRequest request) =>
        ImageProxyHelper.ComputeImageCacheKey(
            request.Url,
            request.MaxWidth,
            request.MaxHeight,
            request.Quality,
            request.Optimize);

    /// <summary>
    /// Captures immutable inputs and retry state for one bounded stale-image refresh series.
    /// </summary>
    private sealed class ImageRefreshSeries
    {
        public string CacheKey { get; }
        public ImageProxyRequest Request { get; }
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public int Attempts { get; set; }

        private readonly CancellationTokenSource _cancellationTokenSource;

        public ImageRefreshSeries(string cacheKey, ImageProxyRequest request)
        {
            CacheKey = cacheKey;
            Request = request;
            ExpiresAtUtc = StartedAtUtc.Add(_refreshSeriesMaxDuration);
            _cancellationTokenSource = new CancellationTokenSource(_refreshSeriesMaxDuration);
        }

        public void CancelForTesting() => _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Waits until a background image refresh series is no longer active.
    /// </summary>
    internal static async Task<bool> WaitForRefreshIdleForTestingAsync(string cacheKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (!_refreshSeries.ContainsKey(cacheKey))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return !_refreshSeries.ContainsKey(cacheKey);
    }

    /// <summary>
    /// Resets static image proxy coordination state between tests.
    /// </summary>
    internal static void ResetStaticStateForTesting()
    {
        foreach (var series in _refreshSeries.Values)
        {
            series.CancelForTesting();
        }

        _refreshSeries.Clear();
        _originWork.Clear();
        SetRefreshRetryDelayForTesting(null);
        SetRefreshSeriesMaxDurationForTesting(null);
        while (_originWorkBudget.CurrentCount < 4)
        {
            _originWorkBudget.Release();
        }
    }

    /// <summary>
    /// Overrides refresh retry delay calculation for deterministic tests.
    /// </summary>
    internal static void SetRefreshRetryDelayForTesting(Func<int, TimeSpan>? delayProvider)
    {
        _refreshRetryDelayProvider = delayProvider ?? CalculateRefreshRetryDelay;
    }

    /// <summary>
    /// Overrides the refresh series deadline for deterministic tests.
    /// </summary>
    internal static void SetRefreshSeriesMaxDurationForTesting(TimeSpan? maxDuration)
    {
        _refreshSeriesMaxDuration = maxDuration ?? RefreshSeriesMaxDuration;
    }
}
