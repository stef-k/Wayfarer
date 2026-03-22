using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Util;

public class TileCacheService
{
    private readonly ILogger<TileCacheService> _logger;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IApplicationSettingsService _applicationSettings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Lock for serializing file system operations across all service instances.
    /// Static because TileCacheService is scoped (per-request) but file operations must be synchronized globally.
    /// </summary>
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// How many tiles to delete from LRU cached storage when the limit has been reached.
    /// </summary>
    private const int LRU_TO_EVICT = 50;

    /// <summary>
    /// Minimum staleness before updating LastAccessed in the database.
    /// Reduces DB writes by ~99% for popular tiles while maintaining adequate LRU precision.
    /// </summary>
    private static readonly TimeSpan LastAccessedThrottleInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default cache expiry when upstream provides no Cache-Control or Expires headers.
    /// OSM tile usage policy requires tiles to be cached for at least 7 days.
    /// </summary>
    private static readonly TimeSpan DefaultCacheExpiry = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _serviceScopeFactory;

    // 1 GB maximum cache size for zoom levels >= 9.
    private readonly int _maxCacheSizeInMB;

    /// <summary>
    /// Tracks the total size of cached tiles in bytes (for zoom >= 9).
    /// Static because cache size must be tracked across all scoped service instances.
    /// Initialized from database on first access via Initialize().
    /// </summary>
    private static long _currentCacheSize = 0;

    /// <summary>
    /// Indicates whether _currentCacheSize has been initialized from the database.
    /// </summary>
    private static volatile bool _cacheSizeInitialized = false;

    /// <summary>
    /// Lock object for one-time cache size initialization.
    /// </summary>
    private static readonly object _initLock = new();

    /// <summary>
    /// Coalesces concurrent re-validation requests for the same tile.
    /// Key: "{z}_{x}_{y}", Value: lazy task that performs exactly one conditional HTTP request.
    /// Prevents duplicate outbound requests to OSM when multiple clients request the same expired tile.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _revalidationFlights = new();

    /// <summary>
    /// In-memory cache of sidecar metadata for zoom 0-8 tiles.
    /// Zoom 0-8 has ~87,000 tiles total; each entry is ~100 bytes (~8.7 MB RAM).
    /// Eliminates disk I/O for sidecar reads on the hot path.
    /// Populated on first read, updated on write.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TileSidecarMetadata> _sidecarCache = new();

    /// <summary>
    /// JSON serializer options for sidecar metadata files.
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Token-bucket rate limiter for outbound requests to upstream tile providers (e.g., OSM).
    /// Prevents cache-miss cascading from overwhelming the upstream server and risking a block
    /// under OSM's fair use policy. Replenishes at a sustained rate of 2 tokens/sec with a
    /// burst capacity of 2 concurrent requests (matching OSM's 2-connection recommendation).
    /// Thread-safe: uses <see cref="SemaphoreSlim"/> for token management and
    /// <see cref="PeriodicTimer"/> for replenishment.
    /// </summary>
    internal static class OutboundBudget
    {
        /// <summary>
        /// Maximum burst capacity — how many outbound requests can fire concurrently.
        /// Set to 2 to comply with OSM tile usage policy ("maximum of 2 download threads").
        /// </summary>
        internal const int BurstCapacity = 2;

        /// <summary>
        /// Replenishment interval — one token is released every this many milliseconds.
        /// 500ms = 2 tokens/sec sustained rate, complying with OSM's fair use policy.
        /// </summary>
        internal const int ReplenishIntervalMs = 500;

        /// <summary>
        /// Maximum time to wait for a token before giving up. Callers that time out
        /// serve stale cache or return 503 (graceful degradation).
        /// </summary>
        internal static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Semaphore representing available outbound tokens. Initialized to <see cref="BurstCapacity"/>.
        /// Each <see cref="AcquireAsync"/> call consumes one token; the replenishment task restores them.
        /// </summary>
        private static readonly SemaphoreSlim _tokens = new(BurstCapacity, BurstCapacity);

        /// <summary>
        /// Cancellation source for stopping the replenishment task during shutdown or testing.
        /// Not disposed on stop — the replenisher task may still hold a reference to its token.
        /// Old instances are abandoned for GC to avoid ObjectDisposedException races.
        /// </summary>
        private static volatile CancellationTokenSource _replenisherCts = new();

        /// <summary>
        /// Ensures the replenishment task is started exactly once, even under concurrent access.
        /// Declared volatile so replacement in <see cref="StopReplenisher"/> is visible across threads.
        /// </summary>
        private static volatile Lazy<Task> _replenisher = new(
            () => StartReplenisher(_replenisherCts.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Attempts to acquire a token for an outbound request. Returns true if a token was
        /// obtained within the timeout, false if the budget is exhausted.
        /// Automatically starts the replenishment background task on first call.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the calling request.</param>
        /// <returns>True if a token was acquired and the outbound request may proceed.</returns>
        internal static async Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
        {
            // Ensure the replenishment task is running (no-op after first call).
            _ = _replenisher.Value;
            return await _tokens.WaitAsync(AcquireTimeout, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a long-running background task that releases one semaphore token every
        /// <see cref="ReplenishIntervalMs"/> milliseconds, maintaining the sustained outbound rate.
        /// Uses <see cref="PeriodicTimer"/> for efficient, non-blocking scheduling.
        /// Stops cleanly when the <paramref name="ct"/> is cancelled (e.g., during app shutdown).
        /// </summary>
        /// <param name="ct">Cancellation token that stops the replenisher loop.</param>
        private static Task StartReplenisher(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ReplenishIntervalMs));
                try
                {
                    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        // Release a token if below capacity. The catch handles the harmless race
                        // where CurrentCount changes between the check and the Release.
                        if (_tokens.CurrentCount < BurstCapacity)
                        {
                            try
                            {
                                _tokens.Release();
                            }
                            catch (SemaphoreFullException)
                            {
                                // Harmless race: another thread released between our check and Release().
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown or test reset — exit cleanly.
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Cancels the current replenishment task and prepares a fresh Lazy so a new replenisher
        /// starts on the next <see cref="AcquireAsync"/> call. Cancels the old CTS first to stop
        /// the running replenisher before creating replacements — this eliminates the brief window
        /// where two replenishers could overlap and double-release tokens.
        /// Does NOT dispose the old CTS — the replenisher task may still reference its token;
        /// abandoned CTS instances are GC'd.
        /// </summary>
        private static void StopReplenisher()
        {
            var oldCts = _replenisherCts;
            oldCts.Cancel();
            _replenisherCts = new CancellationTokenSource();
            _replenisher = new Lazy<Task>(
                () => StartReplenisher(_replenisherCts.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Stops the replenishment task for clean application shutdown. Does not drain or refill
        /// tokens — simply cancels the background task. Called from
        /// <c>IHostApplicationLifetime.ApplicationStopping</c>.
        /// </summary>
        internal static void Stop()
        {
            _replenisherCts.Cancel();
        }

        /// <summary>
        /// Resets the outbound budget for testing. Stops the replenisher, drains and refills
        /// the semaphore to burst capacity, then prepares a fresh replenisher for the next acquire.
        /// Must only be called when no concurrent <see cref="AcquireAsync"/> calls are in flight
        /// (i.e., between tests in a single-threaded setup phase).
        /// </summary>
        internal static void ResetForTesting()
        {
            StopReplenisher();
            // Drain all tokens.
            while (_tokens.CurrentCount > 0)
            {
                _tokens.Wait(0);
            }
            // Refill to burst capacity.
            try
            {
                _tokens.Release(BurstCapacity);
            }
            catch (SemaphoreFullException)
            {
                // Already at capacity after drain — safe to ignore.
            }
        }
    }

    /// <summary>
    /// Stops the outbound budget replenishment task for clean application shutdown.
    /// Call from <c>IHostApplicationLifetime.ApplicationStopping</c> or equivalent.
    /// </summary>
    public static void StopOutboundBudget() => OutboundBudget.Stop();

    /// <summary>
    /// Resets all static state so each test starts with a clean slate.
    /// Must be called between tests to prevent cross-test interference from
    /// <see cref="_revalidationFlights"/>, <see cref="_sidecarCache"/>, and <see cref="_currentCacheSize"/>.
    /// </summary>
    internal static void ResetStaticStateForTesting()
    {
        _revalidationFlights.Clear();
        _sidecarCache.Clear();
        Interlocked.Exchange(ref _currentCacheSize, 0);
        _cacheSizeInitialized = false;
        OutboundBudget.ResetForTesting();
    }

    public TileCacheService(ILogger<TileCacheService> logger, IConfiguration configuration, HttpClient httpClient,
        ApplicationDbContext dbContext, IApplicationSettingsService applicationSettings,
        IServiceScopeFactory serviceScopeFactory, IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _applicationSettings = applicationSettings;
        _serviceScopeFactory = serviceScopeFactory;
        _httpContextAccessor = httpContextAccessor;
        _maxCacheSizeInMB = _applicationSettings.GetSettings().MaxCacheTileSizeInMB;

        if (_maxCacheSizeInMB == -1)
        {
            // -1 means "disable cache size limit" — eviction never triggers.
            _logger.LogInformation(
                "Tile cache size limit disabled (MaxCacheTileSizeInMB = -1). LRU eviction will not run.");
            _maxCacheSizeInMB = int.MaxValue;
        }
        else if (_maxCacheSizeInMB <= 0)
        {
            _logger.LogWarning("Invalid MaxCacheTileSizeInMB value: {MaxCacheTileSizeInMB}. Defaulting to 1024 MB.",
                _maxCacheSizeInMB);
            _maxCacheSizeInMB = 1024; // Default to 1GB
        }
        else if (_maxCacheSizeInMB < 256)
        {
            // OSM tile usage policy requires tiles cached for at least 7 days (minimum 256 MB).
            // The admin UI validates this on save; this warning catches pre-existing DB values.
            _logger.LogWarning(
                "MaxCacheTileSizeInMB ({MaxCacheTileSizeInMB}) is below the OSM-recommended minimum of 256 MB. " +
                "Consider increasing this value in Admin Settings.",
                _maxCacheSizeInMB);
        }

        // Read the cache directory from configuration, fallback to a default if not set.
        _cacheDirectory = _configuration.GetSection("CacheSettings:TileCacheDirectory").Value ?? string.Empty;
        if (string.IsNullOrEmpty(_cacheDirectory))
        {
            _logger.LogWarning("Invalid or missing TileCacheDirectory. Using default path.");
            _cacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), "TileCache");
        }
        else
        {
            // interpret relative paths as "under current directory"
            _cacheDirectory = Path.IsPathRooted(_cacheDirectory)
                ? _cacheDirectory
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _cacheDirectory));
        }

    }

    /// <summary>
    /// Initializes the tile cache by ensuring the cache directory exists and
    /// initializes the current cache size from existing database metadata.
    /// </summary>
    public void Initialize()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
                _logger.LogInformation("TileCache directory created at {CacheDirectory}.", _cacheDirectory);
            }

            // Initialize cache size from database only once across all instances
            InitializeCacheSizeFromDb();
        }
        catch (UnauthorizedAccessException uae)
        {
            _logger.LogError(uae, "Insufficient permissions to create TileCache directory.");
        }
    }

    /// <summary>
    /// Initializes the _currentCacheSize from the database on first access.
    /// Uses double-checked locking to ensure thread-safe one-time initialization.
    /// </summary>
    private void InitializeCacheSizeFromDb()
    {
        if (_cacheSizeInitialized) return;

        lock (_initLock)
        {
            if (_cacheSizeInitialized) return;

            try
            {
                var totalSize = _dbContext.TileCacheMetadata.Sum(t => (long)t.Size);
                Interlocked.Exchange(ref _currentCacheSize, totalSize);
                _cacheSizeInitialized = true;
                _logger.LogInformation("Initialized tile cache size from database: {SizeInMB:F2} MB",
                    totalSize / 1024.0 / 1024.0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize cache size from database. Starting with 0.");
                _cacheSizeInitialized = true; // Mark as initialized to prevent repeated failures
            }
        }
    }

    /// <summary>
    /// Returns the directory where the tile cache is stored, based on appsettings.json
    /// </summary>
    /// <returns></returns>
    public string GetCacheDirectory()
    {
        return _cacheDirectory;
    }

    // ── HTTP request helpers ────────────────────────────────────────────

    /// <summary>
    /// Core tile request method with same-host redirect policy and Referer header.
    /// Acquires an outbound budget token before sending to comply with OSM's fair use policy.
    /// Returns null if the budget is exhausted (callers degrade gracefully with stale cache).
    /// Accepts an optional delegate for customizing request headers (e.g., conditional headers).
    /// </summary>
    private async Task<HttpResponseMessage?> SendTileRequestCoreAsync(string tileUrl,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        // Acquire an outbound request token. If the budget is exhausted, return null
        // so callers can gracefully degrade (serve stale cache or return 503).
        if (!await OutboundBudget.AcquireAsync().ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Outbound request budget exhausted — throttling upstream request for {TileUrl}",
                TileProviderCatalog.RedactApiKey(tileUrl));
            return null;
        }

        const int maxRedirects = 3;
        var initialUri = new Uri(tileUrl);
        var currentUri = initialUri;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

            // OSM requires a Referer header. Derive it from the incoming HTTP request
            // so it automatically matches the public URL (works behind reverse proxies,
            // Cloudflare Tunnel, etc. when forwarded headers are configured).
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx != null)
            {
                request.Headers.Referrer = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            }

            // Let the caller add conditional headers (If-None-Match, If-Modified-Since, etc.)
            configureRequest?.Invoke(request);

            var response = await _httpClient.SendAsync(request);

            if (IsRedirectStatus(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    _logger.LogWarning("Tile response redirected without a Location header: {TileUrl}", TileProviderCatalog.RedactApiKey(tileUrl));
                    response.Dispose();
                    return null;
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                if (!string.Equals(nextUri.Host, initialUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected tile redirect to a different host: {RedirectHost}", nextUri.Host);
                    response.Dispose();
                    return null;
                }

                if (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected tile redirect to non-HTTPS URL: {RedirectUrl}", TileProviderCatalog.RedactApiKey(nextUri.ToString()));
                    response.Dispose();
                    return null;
                }

                response.Dispose();
                currentUri = nextUri;
                continue;
            }

            return response;
        }

        _logger.LogWarning("Rejected tile redirect chain exceeding {MaxRedirects} for {TileUrl}", maxRedirects, TileProviderCatalog.RedactApiKey(tileUrl));
        return null;
    }

    /// <summary>
    /// Sends a tile request without conditional headers.
    /// Sets the Referer header from the current HTTP request to comply with OSM's tile usage policy.
    /// </summary>
    private Task<HttpResponseMessage?> SendTileRequestAsync(string tileUrl)
    {
        return SendTileRequestCoreAsync(tileUrl);
    }

    /// <summary>
    /// Sends a conditional tile request using ETag and/or Last-Modified headers.
    /// Returns the response (caller checks for 304 vs 200).
    /// </summary>
    private Task<HttpResponseMessage?> SendConditionalTileRequestAsync(string tileUrl, string? etag,
        DateTime? lastModified)
    {
        return SendTileRequestCoreAsync(tileUrl, request =>
        {
            if (!string.IsNullOrEmpty(etag))
            {
                // ETags from servers include surrounding quotes; EntityTagHeaderValue handles this.
                if (EntityTagHeaderValue.TryParse(etag, out var etagValue))
                {
                    request.Headers.IfNoneMatch.Add(etagValue);
                }
            }

            if (lastModified.HasValue)
            {
                request.Headers.IfModifiedSince = new DateTimeOffset(lastModified.Value, TimeSpan.Zero);
            }
        });
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.RedirectKeepVerb
            or HttpStatusCode.SeeOther
            or HttpStatusCode.PermanentRedirect;
    }

    // ── Cache expiry parsing ────────────────────────────────────────────

    /// <summary>
    /// Parses Cache-Control max-age and Expires headers from an HTTP response
    /// to determine when the tile should be re-validated.
    /// Falls back to <see cref="DefaultCacheExpiry"/> (7 days) if no cache headers are present,
    /// matching OSM's minimum caching requirement.
    /// </summary>
    internal static DateTime ParseCacheExpiry(HttpResponseMessage response)
    {
        var now = DateTime.UtcNow;

        // 1. Check Cache-Control for max-age=N
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl?.MaxAge is { } maxAge && maxAge > TimeSpan.Zero)
        {
            return now.Add(maxAge);
        }

        // 2. Check Expires header
        if (response.Content.Headers.Expires is { } expires)
        {
            var expiresUtc = expires.UtcDateTime;
            // Only use Expires if it's in the future
            if (expiresUtc > now)
            {
                return expiresUtc;
            }
        }

        // 3. Fallback: 7 days (OSM's minimum caching requirement)
        return now.Add(DefaultCacheExpiry);
    }

    // ── Sidecar metadata helpers (zoom 0-8) ─────────────────────────────

    /// <summary>
    /// Returns the path to the JSON sidecar metadata file for a tile.
    /// Used for zoom 0-8 tiles that are not tracked in the database.
    /// </summary>
    private static string GetSidecarPath(string tileFilePath) => tileFilePath + ".meta";

    /// <summary>
    /// Reads sidecar metadata for a tile. Checks the in-memory cache first,
    /// falls back to disk, and populates the cache on disk hit.
    /// Returns null if no sidecar exists or it is malformed.
    /// </summary>
    private TileSidecarMetadata? ReadSidecarMetadata(string tileFilePath)
    {
        var tileKey = Path.GetFileNameWithoutExtension(tileFilePath);

        // 1. Check in-memory cache first (fast path)
        if (_sidecarCache.TryGetValue(tileKey, out var cached))
        {
            return cached;
        }

        // 2. Fall back to disk
        var sidecarPath = GetSidecarPath(tileFilePath);
        try
        {
            if (!File.Exists(sidecarPath)) return null;
            var json = File.ReadAllText(sidecarPath);
            var meta = JsonSerializer.Deserialize<TileSidecarMetadata>(json, _jsonOptions);
            if (meta != null)
            {
                // Populate in-memory cache for next time
                _sidecarCache.TryAdd(tileKey, meta);
            }

            return meta;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read sidecar metadata for {TileFilePath}", tileFilePath);
            return null;
        }
    }

    /// <summary>
    /// Writes sidecar metadata as a JSON file alongside the tile using rename.
    /// Also updates the in-memory sidecar cache.
    /// Write to .meta.tmp first, then File.Move(overwrite: true).
    /// Note: on Linux (ext4) rename is atomic; on Windows (NTFS) overwrite is delete+rename
    /// which is not atomic — a crash between those steps could lose the metadata file.
    /// This is acceptable because sidecar metadata is regenerated on next access.
    /// </summary>
    private void WriteSidecarMetadata(string tileFilePath, TileSidecarMetadata metadata)
    {
        var sidecarPath = GetSidecarPath(tileFilePath);
        var tmpPath = sidecarPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(metadata, _jsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, sidecarPath, overwrite: true);

            // Update in-memory cache
            var tileKey = Path.GetFileNameWithoutExtension(tileFilePath);
            _sidecarCache[tileKey] = metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write sidecar metadata for {TileFilePath}", tileFilePath);
            // Clean up temp file on failure
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
            catch { /* best effort cleanup */ }
        }
    }

    // ── Tile caching and retrieval ──────────────────────────────────────

    /// <summary>
    /// Downloads a tile from the given URL and caches it on the file system.
    /// Stores ETag, Last-Modified, and computed expiry from upstream response headers.
    /// For zoom levels >= 9, metadata is stored (or updated) in the database.
    /// For zoom levels 0-8, metadata is stored as a JSON sidecar file.
    /// </summary>
    public async Task CacheTileAsync(string tileUrl, string zoomLevel, string xCoordinate, string yCoordinate)
    {
        try
        {
            // Parse parameters
            int zoom = int.Parse(zoomLevel);
            int x = int.Parse(xCoordinate);
            int y = int.Parse(yCoordinate);
            var tileFileName = $"{zoom}_{x}_{y}.png";
            var tileFilePath = Path.Combine(_cacheDirectory, tileFileName);

            // Download the tile with retry logic.
            int retryCount = 3;
            byte[]? tileData = null;
            string? etag = null;
            DateTime? lastModifiedUpstream = null;
            DateTime? expiresAtUtc = null;

            while (retryCount > 0)
            {
                using var response = await SendTileRequestAsync(tileUrl);
                if (response == null)
                {
                    _logger.LogWarning("Tile request was rejected for URL: {TileUrl}", TileProviderCatalog.RedactApiKey(tileUrl));
                    retryCount--;
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    tileData = await response.Content.ReadAsByteArrayAsync();

                    // Extract cache headers from upstream response for conditional request support.
                    etag = response.Headers.ETag?.Tag;
                    lastModifiedUpstream = response.Content.Headers.LastModified?.UtcDateTime;
                    expiresAtUtc = ParseCacheExpiry(response);

                    await _cacheLock.WaitAsync();
                    try
                    {
                        if (!File.Exists(tileFilePath)) // Prevent overwriting existing files
                        {
                            await File.WriteAllBytesAsync(tileFilePath, tileData);
                        }

                        // For zoom < 9, write sidecar in the same lock acquisition as the tile file.
                        // This eliminates TOCTOU where a concurrent reader sees the tile but no metadata.
                        if (zoom < 9)
                        {
                            WriteSidecarMetadata(tileFilePath, new TileSidecarMetadata
                            {
                                ETag = etag,
                                LastModifiedUpstream = lastModifiedUpstream,
                                ExpiresAtUtc = expiresAtUtc
                            });
                        }
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogError(ioEx, "Failed to write tile data to file: {TileFilePath}", tileFilePath);
                        return;
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }

                    _logger.LogInformation("Tile cached at: {TileFilePath}", tileFilePath);
                    break;
                }

                _logger.LogWarning("Attempt failed with status code {StatusCode} for URL: {TileUrl}",
                    response.StatusCode, TileProviderCatalog.RedactApiKey(tileUrl));
                retryCount--;
                if (retryCount == 0)
                {
                    _logger.LogError("Failed to download tile after multiple attempts: {TileUrl}", TileProviderCatalog.RedactApiKey(tileUrl));
                    return;
                }

                // Optional: Delay between retries to avoid rate limiting
                await Task.Delay(500); // 500ms delay between retries
            }

            // For zoom levels >= 9, store or update metadata in the database.
            if (zoom >= 9)
            {
                var existingMetadata = await _dbContext.TileCacheMetadata
                    .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);
                if (existingMetadata == null)
                {
                    // If adding a new tile would exceed the cache limit in Gigabytes, evict tiles.
                    if ((Interlocked.Read(ref _currentCacheSize) + (tileData?.Length ?? 0)) > (_maxCacheSizeInMB * 1024L * 1024L))
                    {
                        await EvictDbTilesAsync();
                    }

                    var tileMetadata = new TileCacheMetadata
                    {
                        Zoom = zoom,
                        X = x,
                        Y = y,
                        // Storing the coordinates as a point (update as needed).
                        TileLocation = new Point(x, y),
                        Size = tileData?.Length ?? 0,
                        TileFilePath = tileFilePath,
                        LastAccessed = DateTime.UtcNow,
                        ETag = etag,
                        LastModifiedUpstream = lastModifiedUpstream,
                        ExpiresAtUtc = expiresAtUtc
                        // Note: RowVersion is handled automatically by EF Core with [Timestamp]
                    };

                    _dbContext.TileCacheMetadata.Add(tileMetadata);
                    await _dbContext.SaveChangesAsync();
                    Interlocked.Add(ref _currentCacheSize, tileData?.Length ?? 0);
                    _logger.LogInformation("Tile metadata stored in database.");
                }
                else
                {
                    // Save the old size for cache size adjustment
                    var oldSize = existingMetadata.Size;
                    // Prepare new values
                    existingMetadata.Size = tileData?.Length ?? 0;
                    existingMetadata.LastAccessed = DateTime.UtcNow;
                    existingMetadata.ETag = etag;
                    existingMetadata.LastModifiedUpstream = lastModifiedUpstream;
                    existingMetadata.ExpiresAtUtc = expiresAtUtc;

                    // Retry loop to handle potential concurrency conflicts.
                    bool updated = false;
                    int attempts = 0;
                    while (!updated && attempts < 3)
                    {
                        attempts++;
                        try
                        {
                            _dbContext.TileCacheMetadata.Update(existingMetadata);
                            await _dbContext.SaveChangesAsync();
                            updated = true;
                            _logger.LogInformation("Tile metadata updated in database on attempt {Attempt}.", attempts);
                        }
                        catch (DbUpdateConcurrencyException ex)
                        {
                            _logger.LogWarning(ex,
                                "Concurrency conflict detected while updating tile metadata. Attempt {Attempt}.",
                                attempts);
                            // Reload the entity from the database.
                            var entry = ex.Entries.Single();
                            var databaseValues = await entry.GetDatabaseValuesAsync();
                            if (databaseValues == null)
                            {
                                _logger.LogError("Tile metadata was deleted by another process.");
                                return;
                            }

                            // Update the local copy with database values and reapply our changes.
                            existingMetadata = (TileCacheMetadata)databaseValues.ToObject();
                            existingMetadata.Size = tileData?.Length ?? 0;
                            existingMetadata.LastAccessed = DateTime.UtcNow;
                            existingMetadata.ETag = etag;
                            existingMetadata.LastModifiedUpstream = lastModifiedUpstream;
                            existingMetadata.ExpiresAtUtc = expiresAtUtc;
                        }
                    }

                    if (!updated)
                    {
                        _logger.LogError(
                            "Failed to update tile metadata after multiple attempts due to concurrency conflicts.");
                        return;
                    }

                    // Adjust the in-memory cache size using the previously saved value.
                    Interlocked.Add(ref _currentCacheSize, (tileData?.Length ?? 0) - oldSize);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching tile from {TileUrl}.", TileProviderCatalog.RedactApiKey(tileUrl));
        }
    }

    /// <summary>
    /// Retrieves a tile from the cache. If the tile exists on disk, checks whether it is
    /// expired and re-validates with the upstream server using conditional requests.
    /// If the file is missing, downloads and caches the tile.
    /// </summary>
    public async Task<byte[]?> RetrieveTileAsync(string zoomLevel, string xCoordinate, string yCoordinate,
        string? tileUrl = null)
    {
        try
        {
            if (!int.TryParse(zoomLevel, out var zoomLvl) ||
                !int.TryParse(xCoordinate, out var xVal) ||
                !int.TryParse(yCoordinate, out var yVal))
            {
                _logger.LogWarning("Invalid tile coordinates: z={Zoom} x={X} y={Y}",
                    zoomLevel, xCoordinate, yCoordinate);
                return null;
            }

            var tileKey = $"{zoomLevel}_{xCoordinate}_{yCoordinate}";
            var tileFileName = $"{tileKey}.png";
            var tileFilePath = Path.Combine(_cacheDirectory, tileFileName);

            // 1. Check the file system first.
            if (File.Exists(tileFilePath))
            {
                _logger.LogDebug("Tile found in cache: {TileFilePath}", tileFilePath);

                // Load metadata to check expiry
                var isExpired = false;
                string? etag = null;
                DateTime? lastModified = null;

                if (zoomLvl >= 9)
                {
                    // Single DB round-trip: load metadata + conditionally update LastAccessed
                    var meta = await LoadAndTouchMetadataAsync(zoomLvl, xVal, yVal);
                    if (meta != null)
                    {
                        if (meta.ExpiresAtUtc == null)
                        {
                            // Legacy tile (pre-migration): no expiry metadata yet.
                            // Assume fresh for 7 days to avoid re-downloading all tiles on deploy.
                            // The first re-validation after 7 days will populate ETag/expiry properly.
                            await SeedLegacyTileExpiryAsync(meta);
                            isExpired = false;
                        }
                        else
                        {
                            isExpired = meta.ExpiresAtUtc <= DateTime.UtcNow;
                        }

                        etag = meta.ETag;
                        lastModified = meta.LastModifiedUpstream;
                    }
                    else
                    {
                        // No DB metadata — treat as expired to populate it
                        isExpired = true;
                    }
                }
                else
                {
                    // Zoom 0-8: check sidecar metadata (in-memory cache first, then disk)
                    var sidecar = ReadSidecarMetadata(tileFilePath);
                    if (sidecar != null)
                    {
                        if (sidecar.ExpiresAtUtc == null)
                        {
                            // Legacy tile (pre-migration): seed 7-day expiry via sidecar.
                            var seeded = new TileSidecarMetadata
                            {
                                ETag = sidecar.ETag,
                                LastModifiedUpstream = sidecar.LastModifiedUpstream,
                                ExpiresAtUtc = DateTime.UtcNow.Add(DefaultCacheExpiry)
                            };
                            WriteSidecarMetadata(tileFilePath, seeded);
                            isExpired = false;
                        }
                        else
                        {
                            isExpired = sidecar.ExpiresAtUtc <= DateTime.UtcNow;
                        }

                        etag = sidecar.ETag;
                        lastModified = sidecar.LastModifiedUpstream;
                    }
                    else
                    {
                        // No sidecar at all — legacy tile with no metadata.
                        // Seed a sidecar with 7-day expiry so next access hits the fast path.
                        WriteSidecarMetadata(tileFilePath, new TileSidecarMetadata
                        {
                            ExpiresAtUtc = DateTime.UtcNow.Add(DefaultCacheExpiry)
                        });
                        isExpired = false;
                    }
                }

                // Fast path: tile is not expired — serve from cache
                if (!isExpired)
                {
                    byte[]? cachedTileData = null;
                    await _cacheLock.WaitAsync();
                    try
                    {
                        // Serialize file reads with purge/write operations.
                        if (File.Exists(tileFilePath))
                        {
                            cachedTileData = await File.ReadAllBytesAsync(tileFilePath);
                        }
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }

                    if (cachedTileData != null) return cachedTileData;
                }

                // Tile is expired — re-validate with upstream (if we have a URL)
                if (!string.IsNullOrEmpty(tileUrl))
                {
                    // Coalesce concurrent re-validations: only ONE HTTP request per expired tile.
                    var flight = _revalidationFlights.GetOrAdd(tileKey,
                        _ => new Lazy<Task<byte[]?>>(
                            () => RevalidateTileAsync(tileUrl, tileFilePath, tileKey, zoomLvl,
                                xVal, yVal, etag, lastModified)));
                    try
                    {
                        var result = await flight.Value;
                        if (result != null) return result;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Re-validation failed for tile {TileKey}, serving stale", tileKey);
                    }
                    finally
                    {
                        // Only remove our own entry (value-checking overload)
                        _revalidationFlights.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]?>>>(tileKey, flight));
                    }
                }

                // Graceful degradation: serve stale cached tile if re-validation failed
                byte[]? staleTileData = null;
                await _cacheLock.WaitAsync();
                try
                {
                    if (File.Exists(tileFilePath))
                    {
                        staleTileData = await File.ReadAllBytesAsync(tileFilePath);
                    }
                }
                finally
                {
                    _cacheLock.Release();
                }

                if (staleTileData != null) return staleTileData;
            }

            // 2. If the tile is not on disk, but we have a URL, attempt to fetch it.
            if (string.IsNullOrEmpty(tileUrl))
            {
                _logger.LogWarning("Tile not found and no URL provided: {TileFilePath}", tileFilePath);
                return null;
            }

            _logger.LogDebug("Tile not in cache. Fetching from: {TileUrl}", TileProviderCatalog.RedactApiKey(tileUrl));
            await CacheTileAsync(tileUrl, zoomLevel, xCoordinate, yCoordinate);

            // After fetching, read the file while holding the lock to prevent race with eviction.
            byte[]? fetchedTileData = null;
            await _cacheLock.WaitAsync();
            try
            {
                if (File.Exists(tileFilePath))
                {
                    fetchedTileData = await File.ReadAllBytesAsync(tileFilePath);
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            return fetchedTileData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tile from cache.");
            return null;
        }
    }

    /// <summary>
    /// Re-validates an expired cached tile by sending a conditional HTTP request.
    /// On 304 Not Modified: updates metadata expiry and serves cached file.
    /// On 200 OK: replaces file on disk and updates all metadata.
    /// On failure: returns null (caller will serve stale cached tile).
    /// Called via the <see cref="_revalidationFlights"/> coalescing dictionary to ensure
    /// exactly one outbound request per expired tile.
    /// Uses its own DB scope because the coalescing pattern means the originating request's
    /// scoped DbContext may be disposed while other callers are still awaiting the result.
    /// </summary>
    private async Task<byte[]?> RevalidateTileAsync(string tileUrl, string tileFilePath, string tileKey,
        int zoom, int x, int y, string? etag, DateTime? lastModified)
    {
        using var response = await SendConditionalTileRequestAsync(tileUrl, etag, lastModified);
        if (response == null)
        {
            _logger.LogWarning("Conditional tile request rejected for {TileUrl}",
                TileProviderCatalog.RedactApiKey(tileUrl));
            return null;
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // 304: tile hasn't changed. Update expiry from response headers.
            var newExpiry = ParseCacheExpiry(response);
            var newEtag = response.Headers.ETag?.Tag ?? etag;

            if (zoom >= 9)
            {
                // Use own scope to avoid disposed DbContext from the originating request.
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await UpdateTileExpiryScopedAsync(dbContext, zoom, x, y, newEtag, lastModified, newExpiry);
            }

            _logger.LogDebug("Tile {TileKey} re-validated (304 Not Modified)", tileKey);

            // Read cached file (and write sidecar for zoom 0-8) under the same lock
            // to prevent a concurrent purge from deleting the sidecar between write and read.
            byte[]? data = null;
            await _cacheLock.WaitAsync();
            try
            {
                if (zoom < 9)
                {
                    WriteSidecarMetadata(tileFilePath, new TileSidecarMetadata
                    {
                        ETag = newEtag,
                        LastModifiedUpstream = lastModified,
                        ExpiresAtUtc = newExpiry
                    });
                }

                if (File.Exists(tileFilePath))
                {
                    data = await File.ReadAllBytesAsync(tileFilePath);
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            return data;
        }

        if (response.IsSuccessStatusCode)
        {
            // 200: tile has changed. Replace file and update metadata.
            var tileData = await response.Content.ReadAsByteArrayAsync();
            var newEtag = response.Headers.ETag?.Tag;
            var newLastModified = response.Content.Headers.LastModified?.UtcDateTime;
            var newExpiry = ParseCacheExpiry(response);

            await _cacheLock.WaitAsync();
            try
            {
                await File.WriteAllBytesAsync(tileFilePath, tileData);

                if (zoom < 9)
                {
                    WriteSidecarMetadata(tileFilePath, new TileSidecarMetadata
                    {
                        ETag = newEtag,
                        LastModifiedUpstream = newLastModified,
                        ExpiresAtUtc = newExpiry
                    });
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            if (zoom >= 9)
            {
                // Use own scope to avoid disposed DbContext from the originating request.
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await UpdateTileAfterRevalidationScopedAsync(dbContext, zoom, x, y, tileData.Length, newEtag,
                    newLastModified, newExpiry);
            }

            _logger.LogDebug("Tile {TileKey} re-validated (200 OK, replaced)", tileKey);
            return tileData;
        }

        _logger.LogWarning("Conditional request returned {StatusCode} for {TileUrl}",
            response.StatusCode, TileProviderCatalog.RedactApiKey(tileUrl));
        return null;
    }

    // ── DB metadata helpers (zoom >= 9) ─────────────────────────────────

    /// <summary>
    /// Seeds a default 7-day expiry on a legacy tile that has no ExpiresAtUtc.
    /// Prevents re-downloading all existing cached tiles on first access after deployment.
    /// The tile will be properly re-validated (with conditional headers) when this expiry passes.
    /// </summary>
    private async Task SeedLegacyTileExpiryAsync(TileCacheMetadata meta)
    {
        meta.ExpiresAtUtc = DateTime.UtcNow.Add(DefaultCacheExpiry);
        try
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogDebug("Seeded 7-day expiry for legacy tile z={Zoom} x={X} y={Y}", meta.Zoom, meta.X, meta.Y);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another instance seeded concurrently; safe to ignore.
            _logger.LogDebug("Legacy expiry seed skipped due to concurrency (non-critical)");
        }
    }

    /// <summary>
    /// Loads tile metadata from the database and conditionally updates LastAccessed
    /// if it is older than <see cref="LastAccessedThrottleInterval"/>.
    /// Combines what were previously two DB round-trips into one.
    /// </summary>
    private async Task<TileCacheMetadata?> LoadAndTouchMetadataAsync(int zoom, int x, int y)
    {
        var meta = await _dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);

        if (meta == null) return null;

        // Throttle LastAccessed updates: only write if older than threshold.
        // Reduces DB writes by ~99% for popular tiles.
        if (meta.LastAccessed < DateTime.UtcNow - LastAccessedThrottleInterval)
        {
            meta.LastAccessed = DateTime.UtcNow;
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another instance updated concurrently; safe to ignore for LastAccessed.
                _logger.LogDebug("LastAccessed update skipped due to concurrency (non-critical)");
            }
        }

        return meta;
    }

    /// <summary>
    /// Updates only the cache expiry metadata after a 304 Not Modified response.
    /// Uses the provided scoped DbContext (safe for use from coalesced tasks).
    /// </summary>
    private async Task UpdateTileExpiryScopedAsync(ApplicationDbContext dbContext, int zoom, int x, int y,
        string? etag, DateTime? lastModified, DateTime newExpiry)
    {
        var meta = await dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);
        if (meta == null) return;

        meta.ETag = etag;
        meta.LastModifiedUpstream = lastModified;
        meta.ExpiresAtUtc = newExpiry;
        meta.LastAccessed = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Expiry update skipped due to concurrency (non-critical)");
        }
    }

    /// <summary>
    /// Updates tile metadata after a 200 OK re-validation response (tile content changed).
    /// Uses the provided scoped DbContext (safe for use from coalesced tasks).
    /// </summary>
    private async Task UpdateTileAfterRevalidationScopedAsync(ApplicationDbContext dbContext, int zoom, int x, int y,
        int newSize, string? etag, DateTime? lastModified, DateTime newExpiry)
    {
        var meta = await dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);
        if (meta == null) return;

        var oldSize = meta.Size;
        meta.Size = newSize;
        meta.ETag = etag;
        meta.LastModifiedUpstream = lastModified;
        meta.ExpiresAtUtc = newExpiry;
        meta.LastAccessed = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync();
            Interlocked.Add(ref _currentCacheSize, newSize - oldSize);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Re-validation metadata update skipped due to concurrency (non-critical)");
        }
    }

    // ── Eviction ────────────────────────────────────────────────────────

    /// <summary>
    /// Evicts the least recently used tiles (in batches) from the database and file system to free up cache space.
    /// </summary>
    private async Task EvictDbTilesAsync()
    {
        // Retrieve a batch of the least recently accessed tiles.
        var tilesToEvict = await _dbContext.TileCacheMetadata
            .OrderBy(t => t.LastAccessed)
            .Take(LRU_TO_EVICT) // Adjust the eviction batch size as needed.
            .ToListAsync();

        // Phase 1: Commit DB deletions first.
        // If SaveChangesAsync fails, no files are deleted — cache stays consistent.
        // If it succeeds but file deletion later fails, orphaned files are harmless
        // and self-correcting (next cache write for that tile overwrites them).
        long totalEvictedSize = 0;
        var filePaths = new List<string>(tilesToEvict.Count);

        foreach (var tile in tilesToEvict)
        {
            _dbContext.TileCacheMetadata.Remove(tile);
            totalEvictedSize += tile.Size;
            filePaths.Add(Path.Combine(_cacheDirectory, $"{tile.Zoom}_{tile.X}_{tile.Y}.png"));
        }

        await _dbContext.SaveChangesAsync();
        // Decrement after successful commit to keep _currentCacheSize consistent with DB reality.
        Interlocked.Add(ref _currentCacheSize, -totalEvictedSize);

        // Phase 2: Delete files (best-effort, after DB commit succeeded).
        foreach (var tileFilePath in filePaths)
        {
            try
            {
                await _cacheLock.WaitAsync();
                try
                {
                    if (File.Exists(tileFilePath))
                    {
                        File.Delete(tileFilePath);
                        _logger.LogInformation("Tile file deleted: {TileFilePath}", tileFilePath);
                    }
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tile file: {TileFilePath}", tileFilePath);
            }
        }

        _logger.LogInformation("Evicted tiles to maintain cache size.");
    }

    // ── Cache statistics ────────────────────────────────────────────────

    /// <summary>
    /// Gets the current file size of the total cache.
    /// </summary>
    public Task<double> GetCacheFileSizeInMbAsync()
    {
        DirectoryInfo di = new DirectoryInfo(_cacheDirectory);
        var totalSizeInBytes = di.GetFiles().Sum(f => f.Length);
        if (totalSizeInBytes <= 0)
        {
            return Task.FromResult(0.0);
        }

        var totalSizeInMb = totalSizeInBytes / 1024.0 / 1024.0;

        return Task.FromResult(totalSizeInMb);
    }


    /// <summary>
    /// Gets the total tile cache size store in file system
    /// </summary>
    /// <returns></returns>
    public Task<int> GetTotalCachedFilesAsync()
    {
        DirectoryInfo di = new DirectoryInfo(_cacheDirectory);
        var totalFiles = di.GetFiles().Count();

        return Task.FromResult(totalFiles);
    }

    /// <summary>
    /// Gets the total tile LRU (Least Recently Used) cache size stored in the database.
    /// LRU cache is cached tiles with zoom levels >= 9.
    /// </summary>
    /// <returns>The total LRU cache size in megabytes.</returns>
    public async Task<double> GetLruCachedInMbFilesAsync()
    {
        var lruSize = await _dbContext.TileCacheMetadata.SumAsync(t => (long)t.Size);

        if (lruSize <= 0)
        {
            return 0.0;
        }

        return lruSize / 1024.0 / 1024.0;
    }

    public async Task<int> GetLruTotalFilesInDbAsync()
    {
        var lruTotalFiles = await _dbContext.TileCacheMetadata.CountAsync();

        return lruTotalFiles;
    }

    // ── Purge operations ────────────────────────────────────────────────

    /// <summary>
    /// Purges all tile cache both static (zoom levels &lt;= 8) and LRU cache (zoom levels &gt;= 9).
    /// Also cleans up sidecar metadata files (.meta) and temporary files (.meta.tmp).
    /// </summary>
    public async Task PurgeAllCacheAsync()
    {
        if (!Directory.Exists(_cacheDirectory)) return;

        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        const int batchSize = 300; // Adjustable batch size for optimal performance
        const int maxRetries = 3; // Max number of retries
        const int delayBetweenRetries = 1000; // Delay between retries in milliseconds
        var filesToDelete = new List<TileCacheMetadata>();

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.png"))
        {
            try
            {
                // Use the full file path for querying DB records.
                var fileToPurge = await dbContext.TileCacheMetadata
                    .Where(t => t.TileFilePath == file)
                    .FirstOrDefaultAsync();

                long fileSize = new FileInfo(file).Length;

                if (File.Exists(file))
                {
                    await _cacheLock.WaitAsync();
                    try
                    {
                        // Serialize file deletes with cache reads/writes.
                        File.Delete(file); // Delete the file from disk
                        Interlocked.Add(ref _currentCacheSize, -fileSize); // Update cache size tracker
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }

                    if (fileToPurge != null)
                    {
                        _logger.LogInformation("Marking file {File} for deletion in DB.", file);
                        // Add the entity to the deletion list.
                        filesToDelete.Add(fileToPurge);
                    }
                    else
                    {
                        _logger.LogWarning("No DB record found for file {File}.", file);
                    }
                }
                else
                {
                    _logger.LogWarning("File not found for deletion: {File}", file);
                }

                // Commit in batches
                if (filesToDelete.Count >= batchSize)
                {
                    await RetryOperationAsync(async () =>
                    {
                        dbContext.TileCacheMetadata.RemoveRange(filesToDelete);
                        var affectedRows = await dbContext.SaveChangesAsync();
                        _logger.LogInformation("Batch commit completed. Rows affected: {Rows}", affectedRows);
                        filesToDelete.Clear();
                    }, maxRetries, delayBetweenRetries);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error purging file {File}", file);
            }
        }

        // Commit any remaining entries if the batch size was not reached
        if (filesToDelete.Any())
        {
            await RetryOperationAsync(async () =>
            {
                dbContext.TileCacheMetadata.RemoveRange(filesToDelete);
                var affectedRows = await dbContext.SaveChangesAsync();
                _logger.LogInformation("Final commit completed. Rows affected: {Rows}", affectedRows);
            }, maxRetries, delayBetweenRetries);
        }

        // Clean up orphan DB records (records without corresponding files on disk)
        var orphanRecords = await dbContext.TileCacheMetadata
            .Where(t => !File.Exists(t.TileFilePath))
            .ToListAsync();

        if (orphanRecords.Any())
        {
            _logger.LogInformation("Found {Count} orphan DB records without files on disk.", orphanRecords.Count);
            await RetryOperationAsync(async () =>
            {
                dbContext.TileCacheMetadata.RemoveRange(orphanRecords);
                var affectedRows = await dbContext.SaveChangesAsync();
                _logger.LogInformation("Orphan records cleanup completed. Rows affected: {Rows}", affectedRows);
            }, maxRetries, delayBetweenRetries);
        }

        // Clean up sidecar metadata files and temp files as a final sweep.
        CleanupSidecarFiles();
    }

    /// <summary>
    /// Removes all sidecar metadata files (.meta) and temporary files (.meta.tmp)
    /// from the cache directory. Also clears the in-memory sidecar cache.
    /// </summary>
    private void CleanupSidecarFiles()
    {
        try
        {
            foreach (var metaFile in Directory.EnumerateFiles(_cacheDirectory, "*.meta"))
            {
                try { File.Delete(metaFile); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete sidecar file {File}", metaFile); }
            }

            foreach (var tmpFile in Directory.EnumerateFiles(_cacheDirectory, "*.meta.tmp"))
            {
                try { File.Delete(tmpFile); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete temp sidecar file {File}", tmpFile); }
            }

            _sidecarCache.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during sidecar file cleanup");
        }
    }

    private async Task RetryOperationAsync(Func<Task> operation, int maxRetries, int delayBetweenRetries)
    {
        int attempt = 0;
        while (attempt < maxRetries)
        {
            try
            {
                await operation();
                break; // Operation succeeded; exit loop.
            }
            catch (Exception e)
            {
                attempt++;
                _logger.LogError(e, "Error during operation, retrying... Attempt {Attempt} of {MaxRetries}", attempt,
                    maxRetries);
                if (attempt >= maxRetries)
                {
                    _logger.LogError("Max retry attempts reached. Operation failed.");
                    throw;
                }

                await Task.Delay(delayBetweenRetries);
            }
        }
    }

    /// <summary>
    /// Purges all LRU tile cache (zoom levels >= 9) from both file system and database.
    /// </summary>
    public async Task PurgeLRUCacheAsync()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        using var transaction = await dbContext.Database.BeginTransactionAsync();

        var lruCache = await dbContext.TileCacheMetadata
            .Where(file => file.Zoom >= 9)
            .AsTracking()
            .ToListAsync();

        var recordsToDelete = new List<TileCacheMetadata>();

        foreach (var file in lruCache)
        {
            try
            {
                if (File.Exists(file.TileFilePath))
                {
                    // Use RetryOperationAsync for file deletion logic
                    await RetryOperationAsync(() =>
                    {
                        return DeleteCacheFileAsync(file.TileFilePath, file.Size);
                    }, 3, 500); // 3 retries, 500ms delay between retries
                }
                else
                {
                    _logger.LogWarning("File not found for deletion: {File}", file.TileFilePath);
                }
                // Always mark DB record for deletion, regardless of whether file existed
                recordsToDelete.Add(file);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing file {File}", file.TileFilePath);
            }
        }

        if (recordsToDelete.Any())
        {
            // Use RetryOperationAsync for database save logic
            await RetryOperationAsync(async () =>
            {
                dbContext.TileCacheMetadata.RemoveRange(recordsToDelete);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }, 3, 1000); // 3 retries, 1000ms delay between retries
        }
        else
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// Deletes a cache file while holding the cache lock to avoid read/write races.
    /// </summary>
    private async Task DeleteCacheFileAsync(string tileFilePath, long tileSize)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (File.Exists(tileFilePath))
            {
                File.Delete(tileFilePath);
                Interlocked.Add(ref _currentCacheSize, -tileSize);
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
