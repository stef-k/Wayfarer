using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Parsers;
using Wayfarer.Util;

public partial class TileCacheService
{
    private readonly ILogger<TileCacheService> _logger;

    /// <summary>
    /// Request-scoped database context injected via constructor.
    /// Safe to use directly in CacheTileAsync/RetrieveTileAsync because TileCacheService is
    /// Transient (one instance per injection via AddHttpClient) and DbContext is Scoped (one per
    /// request). Each request gets its own TileCacheService + DbContext pair — no cross-request sharing.
    /// Background operations (eviction, purge, revalidation) create their own scope via
    /// <see cref="_serviceScopeFactory"/> to avoid disposed-context failures.
    /// </summary>
    private readonly ApplicationDbContext _dbContext;
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IApplicationSettingsService _applicationSettings;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TileMetadataHotCache _tileMetadataHotCache;

    /// <summary>
    /// Lock for serializing file write and delete operations across all service instances.
    /// Read operations proceed without locking and catch <see cref="IOException"/> as a cache miss
    /// (file may have been deleted by a concurrent eviction or purge).
    /// Static because TileCacheService is scoped (per-request) but file operations must be synchronized globally.
    /// </summary>
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// How many tiles to delete from LRU cached storage when the limit has been reached.
    /// </summary>
    private const int LRU_TO_EVICT = 50;

    /// <summary>
    /// Zoom levels at or above this threshold use database-backed metadata.
    /// Zoom levels below this use JSON sidecar files on disk (fewer tiles, simpler management).
    /// </summary>
    private const int DbMetadataZoomThreshold = 9;

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
    /// Delay seam for the current fixed cold-miss retry policy.
    /// Production retains the existing 500 ms delay; tests may complete it without wall-clock waiting.
    /// </summary>
    private static Func<TimeSpan, CancellationToken, Task> _coldMissRetryDelay =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>
    /// Guards against concurrent eviction runs. Only one eviction can proceed at a time;
    /// concurrent callers skip eviction (the in-progress run will free enough space).
    /// Uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> for lock-free coalescing.
    /// </summary>
    private static int _evictionInProgress = 0;

    /// <summary>
    /// Guards against concurrent purge operations (manual or provider-change triggered).
    /// Only one purge can proceed at a time; concurrent callers receive an
    /// <see cref="InvalidOperationException"/>. HTTP callers surface this as 409 Conflict;
    /// internal callers (e.g. tile-provider-change) skip silently.
    /// Uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> for lock-free rejection.
    /// </summary>
    private static int _purgeInProgress = 0;

    /// <summary>
    /// Indicates whether a cache purge operation is currently running.
    /// </summary>
    public static bool IsPurgeInProgress => Volatile.Read(ref _purgeInProgress) == 1;

    /// <summary>
    /// Indicates whether _currentCacheSize has been initialized from the database.
    /// </summary>
    private static volatile bool _cacheSizeInitialized = false;

    /// <summary>
    /// Lock object for one-time cache size initialization.
    /// </summary>
    private static readonly object _initLock = new();

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
    /// Stops the outbound budget replenishment task for clean application shutdown.
    /// Call from <c>IHostApplicationLifetime.ApplicationStopping</c> or equivalent.
    /// </summary>
    public static void StopOutboundBudget() => OutboundBudget.Stop();

    /// <summary>
    /// Exposes the outbound budget burst capacity for client-side configuration.
    /// Injected into <c>wayfarerTileConfig.burstCapacity</c> by _Layout.cshtml so the
    /// tile layer can derive its concurrency pool size without hardcoding the value.
    /// </summary>
    public static int OutboundBurstCapacity => OutboundBudget.BurstCapacity;

    /// <summary>
    /// Reconciles <see cref="_currentCacheSize"/> with the authoritative database sum.
    /// Called periodically by <see cref="Wayfarer.Jobs.RateLimitCleanupJob"/> to correct drift
    /// from non-atomic size tracking during concurrent eviction/caching operations.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for creating a database context.</param>
    internal static async Task ReconcileCacheSizeAsync(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbSum = await dbContext.TileCacheMetadata.SumAsync(t => (long)t.Size);
        Interlocked.Exchange(ref _currentCacheSize, dbSum);
    }

    /// <summary>
    /// Resets all static state so each test starts with a clean slate.
    /// Must be called between tests to prevent cross-test interference from
    /// <see cref="_refreshSeries"/>, <see cref="_sidecarCache"/>, and <see cref="_currentCacheSize"/>.
    /// </summary>
    internal static void ResetStaticStateForTesting()
    {
        foreach (var series in _refreshSeries.Values)
        {
            series.CancelForTesting();
        }

        _refreshSeries.Clear();
        _sidecarCache.Clear();
        SetRefreshRetryDelayForTesting(null);
        SetColdMissRetryDelayForTesting(null);
        SetTileFileReplacerForTesting(null);
        Interlocked.Exchange(ref _currentCacheSize, 0);
        Interlocked.Exchange(ref _evictionInProgress, 0);
        Interlocked.Exchange(ref _purgeInProgress, 0);
        _cacheSizeInitialized = false;
        OutboundBudget.ResetForTesting();
        TileProviderRetryPolicy.ResetForTesting();
    }

    public TileCacheService(ILogger<TileCacheService> logger, IConfiguration configuration, HttpClient httpClient,
        ApplicationDbContext dbContext, IApplicationSettingsService applicationSettings,
        IServiceScopeFactory serviceScopeFactory, IHttpContextAccessor httpContextAccessor,
        TileMetadataHotCache tileMetadataHotCache)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _applicationSettings = applicationSettings;
        _serviceScopeFactory = serviceScopeFactory;
        _httpContextAccessor = httpContextAccessor;
        _tileMetadataHotCache = tileMetadataHotCache;
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
    /// Returns a typed pre-transport rejection when provider or local capacity blocks the request.
    /// Accepts an optional delegate for customizing request headers (e.g., conditional headers).
    /// </summary>
    private async Task<TileRequestSendResult> SendTileRequestCoreAsync(string tileUrl,
        Action<HttpRequestMessage>? configureRequest = null, bool chargeClientAllowance = true,
        string? clientIp = null, bool allowHttpContext = true, int attemptNumber = 1,
        bool deferCancellationDiagnostic = false, DateTimeOffset? interactiveDeadline = null,
        TileContactState? contactState = null, Action? onClientAllowanceCharged = null,
        CancellationToken callerCancellationToken = default,
        CancellationToken cancellationToken = default)
    {
        // Two-phase per-IP outbound budget: peek first (fast-fail without incrementing),
        // then record the hit only after the global budget is acquired. This prevents
        // budget-exhausted requests from inflating the per-IP counter, which previously
        // caused cascading 503 rejections: on cold-cache loads with ~35 tiles, every
        // request (including those rejected by the global budget) incremented the per-IP
        // counter, so retries found the counter already past the limit and failed immediately.
        // The initiating request charges this allowance once; retries do not charge it again.
        // clientIp may be passed explicitly by callers (e.g., coalesced revalidation) where
        // HttpContext is no longer available; falls back to HttpContext if not provided.
        string? resolvedIpForBudget = null;
        if (chargeClientAllowance)
        {
            var perIpLimit = _applicationSettings.GetSettings().TileOutboundBudgetPerIpPerMinute;
            if (perIpLimit > 0)
            {
                resolvedIpForBudget = clientIp;
                if (resolvedIpForBudget == null && allowHttpContext)
                {
                    var ctx = _httpContextAccessor.HttpContext;
                    if (ctx != null)
                    {
                        resolvedIpForBudget = RateLimitHelper.GetClientIpAddress(ctx);
                    }
                }

                if (resolvedIpForBudget != null && RateLimitHelper.WouldExceedRateLimit(
                        TilesController.OutboundBudgetCache, resolvedIpForBudget, perIpLimit))
                {
                    TileCacheDiagnostics.ClientBudgetRejected(_logger, "outbound-client");
                    _logger.LogWarning(
                        "Per-client outbound tile allowance exceeded; upstream request rejected.");
                    return TileRequestSendResult.Rejected(TileRequestRejection.ClientBudget);
                }
            }
        }

        const int maxRedirects = 3;
        var initialUri = new Uri(tileUrl);
        var currentUri = initialUri;
        var providerKey = TileProviderRetryPolicy.GetProviderKey(tileUrl);
        var requestKind = configureRequest == null ? "unconditional" : "conditional";
        contactState ??= new TileContactState();

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            if (contactState.IsExhausted)
            {
                return TileRequestSendResult.Rejected(TileRequestRejection.ContactLimit);
            }

            var providerDelay = TileProviderRetryPolicy.GetRemainingProviderDelay(providerKey);
            if (providerDelay > TimeSpan.Zero)
            {
                var allowedWait = TileProviderRetryPolicy.MaxIndividualWait;
                if (interactiveDeadline.HasValue)
                {
                    var interactiveRemaining = interactiveDeadline.Value - TileProviderRetryPolicy.UtcNow;
                    allowedWait = interactiveRemaining < allowedWait ? interactiveRemaining : allowedWait;
                }

                if (allowedWait <= TimeSpan.Zero || providerDelay > allowedWait)
                {
                    TileCacheDiagnostics.ProviderDelay(
                        _logger,
                        "gate-rejected",
                        providerDelay.TotalMilliseconds);
                    return TileRequestSendResult.ProviderDeferred(providerDelay);
                }

                TileCacheDiagnostics.ProviderDelay(
                    _logger,
                    "gate-wait",
                    providerDelay.TotalMilliseconds);
                try
                {
                    await _coldMissRetryDelay(providerDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
                {
                    TileCacheDiagnostics.Cancellation(_logger, "provider-not-before-wait");
                    throw;
                }

                var stillBlocked = TileProviderRetryPolicy.GetRemainingProviderDelay(providerKey);
                if (stillBlocked > TimeSpan.Zero)
                {
                    TileCacheDiagnostics.ProviderDelay(
                        _logger,
                        "gate-still-active",
                        stillBlocked.TotalMilliseconds);
                    return TileRequestSendResult.ProviderDeferred(stillBlocked);
                }
            }

            // Every actual provider contact, including redirects and retries, consumes global capacity.
            OutboundBudgetAcquisition acquisition;
            try
            {
                acquisition = await OutboundBudget.AcquireDetailedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
            {
                if (!deferCancellationDiagnostic)
                {
                    TileCacheDiagnostics.Cancellation(_logger, "global-budget-wait");
                }

                throw;
            }

            TileCacheDiagnostics.BudgetWait(
                _logger,
                acquisition.Acquired ? "acquired" : "rejected",
                acquisition.WaitDuration.TotalMilliseconds);
            if (!acquisition.Acquired)
            {
                TileCacheDiagnostics.GlobalBudgetRejected(
                    _logger,
                    "global",
                    acquisition.WaitDuration.TotalMilliseconds);
                _logger.LogWarning("Global outbound tile budget exhausted; upstream request rejected.");
                return TileRequestSendResult.Rejected(TileRequestRejection.GlobalBudget);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

            // OSM requires a Referer header. Derive it from the incoming HTTP request
            // so it automatically matches the public URL (works behind reverse proxies,
            // Cloudflare Tunnel, etc. when forwarded headers are configured).
            var ctx = allowHttpContext ? _httpContextAccessor.HttpContext : null;
            if (ctx != null)
            {
                request.Headers.Referrer = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            }

            // Let the caller add conditional headers (If-None-Match, If-Modified-Since, etc.)
            configureRequest?.Invoke(request);

            // Reserve immediately before transport so retries and redirects share one hard ceiling.
            if (!contactState.TryReserveContact())
            {
                return TileRequestSendResult.Rejected(TileRequestRejection.ContactLimit);
            }

            // Charge once only after this series has admitted its first actual provider contact.
            if (resolvedIpForBudget != null)
            {
                RateLimitHelper.RecordRateLimitHit(TilesController.OutboundBudgetCache, resolvedIpForBudget);
                resolvedIpForBudget = null;
                onClientAllowanceCharged?.Invoke();
            }

            TileCacheDiagnostics.UpstreamAttempt(_logger, requestKind, attemptNumber);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
            {
                if (!deferCancellationDiagnostic)
                {
                    TileCacheDiagnostics.Cancellation(_logger, "upstream-transport");
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                TileCacheDiagnostics.UpstreamFailure(_logger, "transport");
                throw;
            }
            catch (Exception)
            {
                TileCacheDiagnostics.UpstreamFailure(_logger, "transport");
                throw;
            }

            TileCacheDiagnostics.UpstreamStatus(
                _logger,
                requestKind,
                attemptNumber,
                (int)response.StatusCode);

            if (IsRedirectStatus(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    _logger.LogWarning("Tile response redirected without a Location header.");
                    response.Dispose();
                    return TileRequestSendResult.Rejected(TileRequestRejection.InvalidProviderResponse);
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                if (!string.Equals(nextUri.Host, initialUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected tile redirect to a different host.");
                    response.Dispose();
                    return TileRequestSendResult.Rejected(TileRequestRejection.InvalidProviderResponse);
                }

                if (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected tile redirect to a non-HTTPS URL.");
                    response.Dispose();
                    return TileRequestSendResult.Rejected(TileRequestRejection.InvalidProviderResponse);
                }

                response.Dispose();
                currentUri = nextUri;
                continue;
            }

            return TileRequestSendResult.Succeeded(response);
        }

        _logger.LogWarning("Rejected tile redirect chain exceeding {MaxRedirects}.", maxRedirects);
        return TileRequestSendResult.Rejected(TileRequestRejection.InvalidProviderResponse);
    }

    /// <summary>
    /// Sends a tile request without conditional headers.
    /// Sets the Referer header from the current HTTP request to comply with OSM's tile usage policy.
    /// </summary>
    /// <param name="tileUrl">The upstream tile URL.</param>
    /// <param name="chargeClientAllowance">Whether this is the initiating request allowance charge.</param>
    private Task<TileRequestSendResult> SendTileRequestAsync(
        string tileUrl,
        bool chargeClientAllowance = true,
        string? clientIp = null,
        int attemptNumber = 1,
        DateTimeOffset? interactiveDeadline = null,
        TileContactState? contactState = null,
        CancellationToken callerCancellationToken = default,
        CancellationToken cancellationToken = default)
    {
        return SendTileRequestCoreAsync(tileUrl, chargeClientAllowance: chargeClientAllowance,
            clientIp: clientIp, attemptNumber: attemptNumber,
            interactiveDeadline: interactiveDeadline,
            contactState: contactState,
            callerCancellationToken: callerCancellationToken,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends a conditional tile request using ETag and/or Last-Modified headers.
    /// Returns the response (caller checks for 304 vs 200).
    /// </summary>
    private Task<TileRequestSendResult> SendConditionalTileRequestAsync(string tileUrl, string? etag,
        DateTime? lastModified, string? clientIp = null, bool allowHttpContext = true,
        bool chargeClientAllowance = true, int attemptNumber = 1,
        TileContactState? contactState = null, Action? onClientAllowanceCharged = null,
        CancellationToken cancellationToken = default)
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
        }, chargeClientAllowance: chargeClientAllowance, clientIp: clientIp,
            allowHttpContext: allowHttpContext, attemptNumber: attemptNumber,
            deferCancellationDiagnostic: true,
            contactState: contactState,
            onClientAllowanceCharged: onClientAllowanceCharged,
            callerCancellationToken: cancellationToken,
            cancellationToken: cancellationToken);
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

    // TODO #214-D: Investigate pre-warming adjacent zoom levels (z-1, z+1) after a successful
    // fetch to avoid cold-cache penalties when users zoom in/out. Two approaches to evaluate:
    // 1. Fire-and-forget from CacheTileAsync — simplest but competes equally for OutboundBudget tokens.
    // 2. Background Channel<T> queue at lower priority — safer but requires a priority-aware token bucket.
    // Deferred: OutboundBudget currently has no priority mechanism (SemaphoreSlim-based).

    /// <summary>
    /// Downloads a tile from the given URL and caches it on the file system.
    /// Stores ETag, Last-Modified, and computed expiry from upstream response headers.
    /// For zoom levels >= DbMetadataZoomThreshold, metadata is stored (or updated) in the database.
    /// For zoom levels below that, metadata is stored as a JSON sidecar file.
    /// Returns false if the outbound budget was exhausted (tile not downloaded), true otherwise.
    /// </summary>
    public async Task<bool> CacheTileAsync(string tileUrl, string zoomLevel, string xCoordinate, string yCoordinate,
        CancellationToken cancellationToken = default)
    {
        var result = await CacheTileWithRetryAsync(
            tileUrl, zoomLevel, xCoordinate, yCoordinate, cancellationToken);
        return result.Status != TileCacheFillStatus.BudgetRejected;
    }

    /// <summary>Downloads and stores one cold tile while retaining its typed upstream outcome.</summary>
    private async Task<TileCacheFillResult> CacheTileWithRetryAsync(
        string tileUrl,
        string zoomLevel,
        string xCoordinate,
        string yCoordinate,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse parameters
            int zoom = int.Parse(zoomLevel);
            int x = int.Parse(xCoordinate);
            int y = int.Parse(yCoordinate);
            var tileFileName = $"{zoom}_{x}_{y}.png";
            var tileFilePath = Path.Combine(_cacheDirectory, tileFileName);

            var download = await DownloadTileWithRetryAsync(tileUrl, cancellationToken);
            if (download.Status != TileCacheFillStatus.Cached)
            {
                return new TileCacheFillResult(download.Status, download.RetryAfter);
            }

            var tileData = download.TileData!;
            var etag = download.ETag;
            var lastModifiedUpstream = download.LastModifiedUpstream;
            var expiresAtUtc = download.ExpiresAtUtc;
            var cacheWriteOutcome = "preserved-existing";
            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(tileFilePath))
                {
                    await File.WriteAllBytesAsync(tileFilePath, tileData, cancellationToken);
                    cacheWriteOutcome = "stored";
                }

                if (zoom < DbMetadataZoomThreshold)
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
                TileCacheDiagnostics.CacheWriteOutcome(_logger, "failed", zoom);
                _logger.LogError(ioEx, "Failed to write tile data to file: {TileFilePath}", tileFilePath);
                return TileCacheFillResult.Cached();
            }
            finally
            {
                _cacheLock.Release();
            }

            TileCacheDiagnostics.CacheWriteOutcome(_logger, cacheWriteOutcome, zoom);
            _logger.LogInformation("Tile cached at: {TileFilePath}", tileFilePath);

            // For zoom levels >= DbMetadataZoomThreshold, store or update metadata in the database.
            // tileData is guaranteed non-null here — the null cases (budget exhaustion, HTTP failure)
            // return early above.
            if (zoom >= DbMetadataZoomThreshold)
            {
                var existingMetadata = await _dbContext.TileCacheMetadata
                    .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);
                if (existingMetadata == null)
                {
                    // If adding a new tile would exceed the cache limit, evict tiles.
                    // Coalesce: only one eviction runs at a time; concurrent callers skip.
                    if ((Interlocked.Read(ref _currentCacheSize) + tileData.Length) > (_maxCacheSizeInMB * 1024L * 1024L))
                    {
                        if (Interlocked.CompareExchange(ref _evictionInProgress, 1, 0) == 0)
                        {
                            try
                            {
                                await EvictDbTilesAsync();
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _evictionInProgress, 0);
                            }
                        }
                    }

                    var tileMetadata = new TileCacheMetadata
                    {
                        Zoom = zoom,
                        X = x,
                        Y = y,
                        // Storing the coordinates as a point (update as needed).
                        TileLocation = new Point(x, y),
                        Size = tileData.Length,
                        TileFilePath = tileFilePath,
                        LastAccessed = DateTime.UtcNow,
                        ETag = etag,
                        LastModifiedUpstream = lastModifiedUpstream,
                        ExpiresAtUtc = expiresAtUtc
                        // Note: RowVersion is handled automatically by EF Core with [Timestamp]
                    };

                    try
                    {
                        _dbContext.TileCacheMetadata.Add(tileMetadata);
                        await _dbContext.SaveChangesAsync();
                        Interlocked.Add(ref _currentCacheSize, tileData.Length);
                        TrySetHotMetadataEntry(zoom, x, y, tileMetadata);
                        _logger.LogInformation("Tile metadata stored in database.");
                    }
                    catch (DbUpdateException)
                    {
                        // Benign race: another concurrent request already inserted this tile.
                        // The unique index on (Zoom, X, Y) prevents duplicates. The file is already
                        // written and the other request incremented _currentCacheSize.
                        _logger.LogDebug(
                            "Tile metadata insert skipped due to concurrent insert (non-critical) z={Zoom} x={X} y={Y}",
                            zoom, x, y);

                        var persistedMetadata = await _dbContext.TileCacheMetadata
                            .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y, cancellationToken);
                        if (persistedMetadata != null)
                        {
                            TrySetHotMetadataEntry(zoom, x, y, persistedMetadata);
                        }
                    }
                }
                else
                {
                    // Save the old size for cache size adjustment
                    var oldSize = existingMetadata.Size;
                    // Prepare new values
                    existingMetadata.Size = tileData.Length;
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
                            // Reload the tracked entity from the database to resolve the conflict.
                            // Using ReloadAsync instead of ToObject() to avoid creating an untracked
                            // entity that would conflict with the original tracked instance.
                            var entry = ex.Entries.Single();
                            var databaseValues = await entry.GetDatabaseValuesAsync();
                            if (databaseValues == null)
                            {
                                _logger.LogError("Tile metadata was deleted by another process.");
                                return TileCacheFillResult.Cached();
                            }

                            await entry.ReloadAsync();
                            existingMetadata = (TileCacheMetadata)entry.Entity;
                            existingMetadata.Size = tileData.Length;
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
                        return TileCacheFillResult.Cached();
                    }

                    // Adjust the in-memory cache size using the previously saved value.
                    Interlocked.Add(ref _currentCacheSize, tileData.Length - oldSize);
                    TrySetHotMetadataEntry(zoom, x, y, existingMetadata);
                }
            }

            return TileCacheFillResult.Cached();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("Error caching tile without recording provider exception details.");
            return TileCacheFillResult.Transient(TileProviderRetryPolicy.FallbackDelayCap);
        }
    }

    /// <summary>
    /// Overrides only the wait mechanism for the existing fixed cold-miss retry delay in tests.
    /// </summary>
    internal static void SetColdMissRetryDelayForTesting(
        Func<TimeSpan, CancellationToken, Task>? delayProvider) =>
        _coldMissRetryDelay = delayProvider ??
            ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));

    /// <summary>
    /// Retrieves a tile from the cache. If the tile exists on disk, checks whether it is
    /// expired and re-validates with the upstream server using conditional requests.
    /// If the file is missing, downloads and caches the tile.
    /// Returns a <see cref="TileRetrievalResult"/> distinguishing success, not-found, and throttled states.
    /// </summary>
    public async Task<TileRetrievalResult> RetrieveTileAsync(string zoomLevel, string xCoordinate, string yCoordinate,
        string? tileUrl = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Capture client IP eagerly while HttpContext is available.
            // Coalesced revalidation flights may execute after the originating request completes,
            // at which point HttpContext is null. Passing the IP explicitly ensures the per-IP
            // outbound budget check works for revalidation requests.
            var httpContext = _httpContextAccessor.HttpContext;
            var clientIp = httpContext != null ? RateLimitHelper.GetClientIpAddress(httpContext) : null;

            if (!int.TryParse(zoomLevel, out var zoomLvl) ||
                !int.TryParse(xCoordinate, out var xVal) ||
                !int.TryParse(yCoordinate, out var yVal))
            {
                _logger.LogWarning("Invalid tile coordinates: z={Zoom} x={X} y={Y}",
                    zoomLevel, xCoordinate, yCoordinate);
                return TileRetrievalResult.NotFound();
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
                var servedByFreshHotMetadata = false;

                if (zoomLvl >= DbMetadataZoomThreshold)
                {
                    if (TryGetHotMetadataEntry(zoomLvl, xVal, yVal, out var hotMetadata) && hotMetadata != null)
                    {
                        etag = hotMetadata.ETag;
                        lastModified = hotMetadata.LastModifiedUpstream;

                        if (hotMetadata.ExpiresAtUtc == null)
                        {
                            // Null expiry is legacy metadata; seed and continue via the authoritative DB path.
                            var seededMetadata = await LoadAndTouchMetadataAsync(zoomLvl, xVal, yVal);
                            if (seededMetadata != null)
                            {
                                if (seededMetadata.ExpiresAtUtc == null)
                                {
                                    await SeedLegacyTileExpiryAsync(seededMetadata);
                                }

                                isExpired = seededMetadata.ExpiresAtUtc <= DateTime.UtcNow;
                                etag = seededMetadata.ETag;
                                lastModified = seededMetadata.LastModifiedUpstream;
                                TrySetHotMetadataEntry(zoomLvl, xVal, yVal, seededMetadata);
                            }
                            else
                            {
                                isExpired = true;
                            }
                        }
                        else
                        {
                            isExpired = hotMetadata.ExpiresAtUtc <= DateTime.UtcNow;
                            servedByFreshHotMetadata = !isExpired;
                        }
                    }
                    else
                    {
                        // Hot-cache miss: fall back to the authoritative DB path and seed the hot cache lazily.
                        var meta = await LoadAndTouchMetadataAsync(zoomLvl, xVal, yVal);
                        if (meta != null)
                        {
                            if (meta.ExpiresAtUtc == null)
                            {
                                await SeedLegacyTileExpiryAsync(meta);
                                TrySetHotMetadataEntry(zoomLvl, xVal, yVal, meta);
                                isExpired = false;
                            }
                            else
                            {
                                isExpired = meta.ExpiresAtUtc <= DateTime.UtcNow;
                                TrySetHotMetadataEntry(zoomLvl, xVal, yVal, meta);
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

                // Fast path: tile is not expired — serve from cache.
                // No lock needed for reads: if a concurrent eviction/purge deletes the file
                // between File.Exists and ReadAllBytesAsync, the IOException is caught and
                // treated as a cache miss (falls through to re-fetch).
                if (!isExpired)
                {
                    byte[]? cachedTileData = null;
                    try
                    {
                        if (File.Exists(tileFilePath))
                        {
                            cachedTileData = await File.ReadAllBytesAsync(tileFilePath);
                        }
                    }
                    catch (IOException)
                    {
                        // File deleted by concurrent eviction/purge — treat as cache miss.
                    }

                    if (cachedTileData != null)
                    {
                        if (servedByFreshHotMetadata)
                        {
                            await TouchLastAccessedFromHotHitAsync(zoomLvl, xVal, yVal);
                        }

                        TileCacheDiagnostics.FreshCacheHit(_logger, "fresh", zoomLvl);
                        return TileRetrievalResult.Success(cachedTileData);
                    }

                    if (servedByFreshHotMetadata)
                    {
                        TryRemoveHotMetadataEntry(zoomLvl, xVal, yVal);
                    }
                }

                // Tile is expired — serve the existing local file immediately and refresh in
                // the background. Revalidation must not sit on the user-facing response path
                // while a complete cached file exists locally.
                if (!string.IsNullOrEmpty(tileUrl))
                {
                    ScheduleBackgroundRefresh(tileUrl, tileFilePath, tileKey, zoomLvl, xVal, yVal,
                        etag, lastModified, clientIp);
                }

                // Graceful degradation: serve stale cached tile even when budget is exhausted
                // or the background refresh cannot start. No lock needed for reads (see
                // fast-path comment above).
                // No lock needed for reads (see fast-path comment above).
                byte[]? staleTileData = null;
                try
                {
                    if (File.Exists(tileFilePath))
                    {
                        staleTileData = await File.ReadAllBytesAsync(tileFilePath);
                    }
                }
                catch (IOException)
                {
                    // File deleted by concurrent eviction/purge — treat as cache miss.
                }

                if (staleTileData != null)
                {
                    if (zoomLvl >= DbMetadataZoomThreshold)
                    {
                        await TouchLastAccessedFromHotHitAsync(zoomLvl, xVal, yVal);
                    }

                    TileCacheDiagnostics.StaleCacheHit(_logger, "stale", zoomLvl);
                    return TileRetrievalResult.Success(staleTileData);
                }
            }

            // 2. If the tile is not on disk, but we have a URL, attempt to fetch it.
            if (string.IsNullOrEmpty(tileUrl))
            {
                _logger.LogWarning("Tile not found and no URL provided: {TileFilePath}", tileFilePath);
                return TileRetrievalResult.NotFound();
            }

            TileCacheDiagnostics.ColdCacheMiss(_logger, "miss", zoomLvl);
            _logger.LogDebug("Tile not in cache; starting controlled upstream fetch.");
            var fillResult = await CacheTileWithRetryAsync(
                tileUrl, zoomLevel, xCoordinate, yCoordinate, cancellationToken);

            // After fetching, read the file. No lock needed for reads (see fast-path comment above).
            byte[]? fetchedTileData = null;
            try
            {
                if (File.Exists(tileFilePath))
                {
                    fetchedTileData = await File.ReadAllBytesAsync(tileFilePath);
                }
            }
            catch (IOException)
            {
                // File deleted by concurrent eviction/purge — treat as cache miss.
            }

            if (fetchedTileData != null)
                return TileRetrievalResult.Success(fetchedTileData);

            return fillResult.Status switch
            {
                TileCacheFillStatus.NotFound => TileRetrievalResult.NotFound(),
                TileCacheFillStatus.PermanentFailure => TileRetrievalResult.PermanentFailure(),
                TileCacheFillStatus.BudgetRejected => TileRetrievalResult.Throttled(
                    TilesController.BudgetRetryAfterSeconds),
                _ => TileRetrievalResult.TransientFailure(
                    TileProviderRetryPolicy.GetBoundedRetryAfterSeconds(fillResult.RetryAfter))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("Error retrieving tile from cache.");
            return TileRetrievalResult.TransientFailure(
                TileProviderRetryPolicy.GetBoundedRetryAfterSeconds(
                    TileProviderRetryPolicy.FallbackDelayCap));
        }
    }

    /// <summary>
    /// Re-validates an expired cached tile by sending a conditional HTTP request.
    /// On 304 Not Modified: updates metadata expiry and serves cached file.
    /// On 200 OK: replaces file on disk and updates all metadata.
    /// Returns a typed refresh outcome while callers continue serving stale cached bytes.
    /// Called from the bounded <see cref="_refreshSeries"/> coordinator to ensure at most
    /// one active refresh series exists per expired tile.
    /// Uses its own DB scope because the coalescing pattern means the originating request's
    /// scoped DbContext may be disposed while other callers are still awaiting the result.
    /// </summary>
    private async Task<StaleRefreshOutcome> RevalidateTileAsync(TileRefreshSeries series)
    {
        var sendResult = await SendConditionalTileRequestAsync(
            series.TileUrl,
            series.ETag,
            series.LastModified,
            series.ClientIp,
            allowHttpContext: false,
            chargeClientAllowance: !series.ClientAllowanceCharged,
            attemptNumber: series.Attempts,
            contactState: series.ContactState,
            onClientAllowanceCharged: () => series.ClientAllowanceCharged = true,
            cancellationToken: series.CancellationToken);
        if (sendResult.Response == null)
        {
            TileCacheDiagnostics.StaleRefreshRejected(_logger, "rejected", series.Zoom);
            _logger.LogWarning("Conditional tile request rejected before upstream transport.");
            return sendResult.Rejection == TileRequestRejection.ContactLimit
                ? StaleRefreshOutcome.Transient
                : StaleRefreshOutcome.PreTransportRejected;
        }

        using var response = sendResult.Response;
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var providerKey = TileProviderRetryPolicy.GetProviderKey(series.TileUrl);
            var providerDelay = TileProviderRetryPolicy.ApplyRetryAfter(providerKey, response);
            if (providerDelay.Kind != ProviderDelayKind.Missing)
            {
                TileCacheDiagnostics.ProviderDelay(
                    _logger,
                    providerDelay.Kind == ProviderDelayKind.Valid
                        ? "provider-directed"
                        : "invalid-provider-value",
                    providerDelay.Delay.TotalMilliseconds);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // 304: tile hasn't changed. Update expiry from response headers.
            var newExpiry = ParseCacheExpiry(response);
            var newEtag = response.Headers.ETag?.Tag ?? series.ETag;

            if (series.Zoom >= DbMetadataZoomThreshold)
            {
                // Use own scope to avoid disposed DbContext from the originating request.
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await UpdateTileExpiryScopedAsync(
                    dbContext,
                    series.Zoom,
                    series.X,
                    series.Y,
                    newEtag,
                    series.LastModified,
                    newExpiry);
            }

            if (series.Zoom < DbMetadataZoomThreshold)
            {
                // Keep the low-zoom sidecar update serialized with other cache file operations.
                await _cacheLock.WaitAsync();
                try
                {
                    WriteSidecarMetadata(series.TileFilePath, new TileSidecarMetadata
                    {
                        ETag = newEtag,
                        LastModifiedUpstream = series.LastModified,
                        ExpiresAtUtc = newExpiry
                    });
                }
                finally
                {
                    _cacheLock.Release();
                }
            }

            TileCacheDiagnostics.ConditionalResponseOutcome(
                _logger,
                "not-modified",
                (int)response.StatusCode);
            TileCacheDiagnostics.CacheWriteOutcome(_logger, "revalidated", series.Zoom);
            _logger.LogDebug("Tile {TileKey} re-validated (304 Not Modified)", series.TileKey);
            return StaleRefreshOutcome.Completed;
        }

        if (response.IsSuccessStatusCode)
        {
            // 200: tile has changed. Replace file and update metadata.
            var tileData = await response.Content.ReadAsByteArrayAsync(series.CancellationToken);
            var newEtag = response.Headers.ETag?.Tag;
            var newLastModified = response.Content.Headers.LastModified?.UtcDateTime;
            var newExpiry = ParseCacheExpiry(response);
            var tempFilePath = CreateTempTilePath(series.TileFilePath);

            await _cacheLock.WaitAsync();
            try
            {
                await File.WriteAllBytesAsync(tempFilePath, tileData, series.CancellationToken);
                ReplaceTileFileAtomically(tempFilePath, series.TileFilePath);

                if (series.Zoom < DbMetadataZoomThreshold)
                {
                    WriteSidecarMetadata(series.TileFilePath, new TileSidecarMetadata
                    {
                        ETag = newEtag,
                        LastModifiedUpstream = newLastModified,
                        ExpiresAtUtc = newExpiry
                    });
                }
            }
            catch
            {
                TryDeleteTempTile(tempFilePath);
                throw;
            }
            finally
            {
                _cacheLock.Release();
            }

            if (series.Zoom >= DbMetadataZoomThreshold)
            {
                // Use own scope to avoid disposed DbContext from the originating request.
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await UpdateTileAfterRevalidationScopedAsync(
                    dbContext,
                    series.Zoom,
                    series.X,
                    series.Y,
                    tileData.Length,
                    newEtag,
                    newLastModified,
                    newExpiry);
            }

            TileCacheDiagnostics.ConditionalResponseOutcome(
                _logger,
                "replaced",
                (int)response.StatusCode);
            TileCacheDiagnostics.CacheWriteOutcome(_logger, "replaced", series.Zoom);
            _logger.LogDebug("Tile {TileKey} re-validated (200 OK, replaced)", series.TileKey);
            return StaleRefreshOutcome.Completed;
        }

        var outcome = IsPermanentUpstreamClientFailure(response.StatusCode)
            ? StaleRefreshOutcome.Terminal
            : StaleRefreshOutcome.Transient;
        TileCacheDiagnostics.ConditionalResponseOutcome(
            _logger,
            outcome == StaleRefreshOutcome.Terminal ? "terminal-status" : "transient-status",
            (int)response.StatusCode);
        _logger.LogWarning("Conditional request returned {StatusCode}.", response.StatusCode);
        return outcome;
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
            TrySetHotMetadataEntry(meta.Zoom, meta.X, meta.Y, meta);
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
            TrySetHotMetadataEntry(zoom, x, y, meta);
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
            TrySetHotMetadataEntry(zoom, x, y, meta);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Re-validation metadata update skipped due to concurrency (non-critical)");
        }
    }

    // ── Eviction ────────────────────────────────────────────────────────

    /// <summary>
    /// Evicts the least recently used tiles (in batches) from the database and file system to free up cache space.
    /// Uses its own <see cref="IServiceScope"/> to avoid lifecycle issues with the per-request DbContext.
    /// Guarded by <see cref="_evictionInProgress"/> to prevent concurrent eviction runs.
    /// </summary>
    private async Task EvictDbTilesAsync()
    {
        // Use a dedicated scope so eviction is independent of the calling request's DbContext lifecycle.
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Retrieve a batch of the least recently accessed tile IDs and sizes.
        // AsNoTracking + projection avoids loading full entities or RowVersions.
        var tilesToEvict = await dbContext.TileCacheMetadata
            .OrderBy(t => t.LastAccessed)
            .Take(LRU_TO_EVICT)
            .AsNoTracking()
            .Select(t => new { t.Id, t.Zoom, t.X, t.Y, t.Size })
            .ToListAsync();

        // Phase 1: Commit DB deletions first.
        // If the delete fails, no files are deleted — cache stays consistent.
        // If it succeeds but file deletion later fails, orphaned files are harmless
        // and self-correcting (next cache write for that tile overwrites them).
        var filePaths = tilesToEvict
            .Select(t => new
            {
                t.Zoom,
                t.X,
                t.Y,
                FilePath = Path.Combine(_cacheDirectory, $"{t.Zoom}_{t.X}_{t.Y}.png")
            })
            .ToList();
        var tileIds = tilesToEvict.Select(t => t.Id).ToList();

        try
        {
            // Re-fetch by ID to get tracked entities with current RowVersion.
            // Compute totalEvictedSize from the re-fetched entities (not the initial projection)
            // to minimize drift when tile sizes change between the two queries.
            var toDelete = await dbContext.TileCacheMetadata
                .Where(t => tileIds.Contains(t.Id))
                .ToListAsync();
            long totalEvictedSize = toDelete.Sum(t => (long)t.Size);
            dbContext.TileCacheMetadata.RemoveRange(toDelete);
            await dbContext.SaveChangesAsync();
            // Decrement after successful commit to keep _currentCacheSize consistent with DB reality.
            Interlocked.Add(ref _currentCacheSize, -totalEvictedSize);
            _logger.LogInformation("Evicted {Count} tiles from database.", toDelete.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Eviction DB delete failed — tiles were not evicted");
            return; // Don't decrement _currentCacheSize; rows were not deleted.
        }

        // Phase 2: Delete files (best-effort, after DB commit succeeded).
        // Single lock acquisition for the entire batch to avoid convoy effects
        // where per-file locking serializes all concurrent writes during eviction.
        await _cacheLock.WaitAsync();
        try
        {
            foreach (var tile in filePaths)
            {
                try
                {
                    TryRemoveHotMetadataEntry(tile.Zoom, tile.X, tile.Y);
                    if (File.Exists(tile.FilePath))
                    {
                        File.Delete(tile.FilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete tile file: {TileFilePath}", tile.FilePath);
                }
            }
        }
        finally
        {
            _cacheLock.Release();
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
    public async Task PurgeAllCacheAsync(SseService? sseService = null, string? sseChannel = null)
    {
        if (Interlocked.CompareExchange(ref _purgeInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A cache purge is already in progress.");

        // Broadcast "started" only after the guard is acquired — ensures no dangling
        // "started" event if a concurrent request loses the CompareExchange race.
        await BroadcastPurgeProgressAsync(sseService, sseChannel, "started", "all", 0, 0);

        try
        {
            if (!Directory.Exists(_cacheDirectory)) return;

            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            const int batchSize = 300; // Adjustable batch size for optimal performance
            const int maxRetries = 3; // Max number of retries
            const int delayBetweenRetries = 1000; // Delay between retries in milliseconds

            // Bulk-load all DB metadata into a dictionary keyed by file path.
            // This replaces O(N) individual DB queries (one per file) with a single query,
            // preventing connection pool exhaustion on large caches (100K+ tiles).
            // Uses foreach instead of ToDictionary to handle anomalous duplicate TileFilePath
            // values gracefully (last-wins) instead of throwing ArgumentException.
            var allMetadataList = await dbContext.TileCacheMetadata
                .AsNoTracking()
                .Select(t => new { t.Id, t.TileFilePath })
                .ToListAsync();
            var allMetadata = new Dictionary<string, int>(allMetadataList.Count);
            foreach (var t in allMetadataList)
            {
                allMetadata[t.TileFilePath ?? string.Empty] = t.Id;
            }

            // Count total files for progress reporting.
            var allFiles = Directory.EnumerateFiles(_cacheDirectory, "*.png").ToList();
            var totalFiles = allFiles.Count;
            var deletedFiles = 0;

            await BroadcastPurgeProgressAsync(sseService, sseChannel, "progress", "all", 0, totalFiles);

            // Collect files and their DB metadata into batches.
            // DB deletions are committed first (consistent with EvictDbTilesAsync ordering).
            // If DB commit fails, no files are deleted — cache stays consistent.
            var batch = new List<(int? MetaId, string FilePath, long FileSize)>();

            foreach (var file in allFiles)
            {
                try
                {
                    int? metaId = allMetadata.TryGetValue(file, out var id) ? id : null;

                    long fileSize = File.Exists(file) ? new FileInfo(file).Length : 0;
                    batch.Add((metaId, file, fileSize));

                    // Commit and delete in batches.
                    if (batch.Count >= batchSize)
                    {
                        await PurgeBatchAsync(dbContext, batch, maxRetries, delayBetweenRetries);
                        deletedFiles += batch.Count;
                        batch.Clear();
                        await BroadcastPurgeProgressAsync(sseService, sseChannel, "progress", "all",
                            deletedFiles, totalFiles);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error purging file {File}", file);
                }
            }

            // Commit any remaining entries if the batch size was not reached.
            if (batch.Any())
            {
                await PurgeBatchAsync(dbContext, batch, maxRetries, delayBetweenRetries);
                deletedFiles += batch.Count;
                batch.Clear();
                await BroadcastPurgeProgressAsync(sseService, sseChannel, "progress", "all",
                    deletedFiles, totalFiles);
            }

            // Clean up orphan DB records (records without corresponding files on disk).
            // File.Exists cannot be translated to SQL, so project only Id + TileFilePath
            // with AsNoTracking to minimize memory, then filter client-side with a HashSet.
            var existingFiles = new HashSet<string>(
                Directory.EnumerateFiles(_cacheDirectory, "*.png"));
            var allPaths = await dbContext.TileCacheMetadata
                .AsNoTracking()
                .Select(t => new { t.Id, t.TileFilePath })
                .ToListAsync();
            var orphanIds = allPaths
                .Where(t => !existingFiles.Contains(t.TileFilePath))
                .Select(t => t.Id)
                .ToList();

            if (orphanIds.Any())
            {
                _logger.LogInformation("Found {Count} orphan DB records without files on disk.", orphanIds.Count);
                await RetryOperationAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();
                    var toDelete = await dbContext.TileCacheMetadata
                        .Where(t => orphanIds.Contains(t.Id))
                        .ToListAsync();
                    if (toDelete.Any())
                    {
                        dbContext.TileCacheMetadata.RemoveRange(toDelete);
                        var affectedRows = await dbContext.SaveChangesAsync();
                        _logger.LogInformation("Orphan records cleanup completed. Rows affected: {Rows}", affectedRows);
                    }
                }, maxRetries, delayBetweenRetries);
            }

            // Clean up sidecar metadata files and temp files as a final sweep.
            CleanupSidecarFiles();
            TryClearHotMetadataCache();
        }
        finally
        {
            Interlocked.Exchange(ref _purgeInProgress, 0);
        }
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

    /// <summary>
    /// Processes a purge batch: commits DB deletions first, then deletes files from disk.
    /// Consistent with <see cref="EvictDbTilesAsync"/> ordering — if DB commit fails,
    /// no files are deleted and cache stays consistent.
    /// Uses lightweight (MetaId, FilePath, FileSize) tuples instead of full entities
    /// to minimize memory usage during large purge operations.
    /// </summary>
    private async Task PurgeBatchAsync(ApplicationDbContext dbContext,
        List<(int? MetaId, string FilePath, long FileSize)> batch,
        int maxRetries, int delayBetweenRetries)
    {
        // Phase 1: Commit DB deletions first.
        // Re-fetches entities by ID inside the retry lambda so that each attempt starts
        // with a clean change tracker and freshly tracked entities — prevents
        // InvalidOperationException from retrying RemoveRange on already-Deleted entities.
        // Captures actual sizes from re-fetched entities (not stale projected sizes from the
        // initial bulk load) so Phase 2's _currentCacheSize decrement is accurate.
        var ids = batch.Where(b => b.MetaId != null).Select(b => b.MetaId!.Value).ToList();
        var actualSizes = new Dictionary<int, long>();
        if (ids.Any())
        {
            await RetryOperationAsync(async () =>
            {
                dbContext.ChangeTracker.Clear();
                var toDelete = await dbContext.TileCacheMetadata
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync();
                if (toDelete.Any())
                {
                    // Capture sizes before deletion — these reflect the current DB state,
                    // not the stale sizes from the initial bulk-load projection.
                    actualSizes = toDelete.ToDictionary(t => t.Id, t => (long)t.Size);
                    dbContext.TileCacheMetadata.RemoveRange(toDelete);
                    var affectedRows = await dbContext.SaveChangesAsync();
                    _logger.LogInformation("Purge batch DB commit completed. Rows affected: {Rows}", affectedRows);
                }
            }, maxRetries, delayBetweenRetries);
        }

        // Phase 2: Delete files from disk (best-effort, after DB commit succeeded).
        // Chunked lock acquisition (10 files per lock) to minimize contention with
        // CacheTileAsync writes during large purge operations.
        // Only decrement _currentCacheSize for DB-tracked tiles (zoom >= 9, Meta != null).
        // Zoom 0-8 tiles are not tracked in _currentCacheSize, so decrementing them
        // would drive the counter negative and permanently disable eviction.
        // Uses actualSizes from re-fetched entities to minimize drift.
        const int deleteChunkSize = 10;
        foreach (var chunk in batch.Chunk(deleteChunkSize))
        {
            await _cacheLock.WaitAsync();
            try
            {
                foreach (var (metaId, filePath, fileSize) in chunk)
                {
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            if (metaId != null && actualSizes.TryGetValue(metaId.Value, out var actualSize))
                            {
                                Interlocked.Add(ref _currentCacheSize, -actualSize);
                                TryRemoveHotMetadataEntryFromPath(filePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete purged file: {File}", filePath);
                    }
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            // Yield after each chunk to give CacheTileAsync callers a chance to acquire
            // the lock, preventing writer starvation during large purge operations.
            await Task.Yield();
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
            catch (DbUpdateException e)
            {
                attempt++;
                _logger.LogWarning(e, "Transient DB error during operation, retrying... Attempt {Attempt} of {MaxRetries}", attempt,
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
    /// Uses DB-first ordering consistent with <see cref="EvictDbTilesAsync"/>:
    /// commit DB deletions first, then delete files. If DB fails, no files are deleted.
    /// Deletes are chunked (1000 IDs per batch) to avoid PostgreSQL query plan explosion
    /// from large IN clauses.
    /// </summary>
    public async Task PurgeLRUCacheAsync(SseService? sseService = null, string? sseChannel = null)
    {
        if (Interlocked.CompareExchange(ref _purgeInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A cache purge is already in progress.");

        // Broadcast "started" only after the guard is acquired — ensures no dangling
        // "started" event if a concurrent request loses the CompareExchange race.
        await BroadcastPurgeProgressAsync(sseService, sseChannel, "started", "lru", 0, 0);

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Project only the fields needed — AsNoTracking avoids change tracker overhead.
            var lruCache = await dbContext.TileCacheMetadata
                .AsNoTracking()
                .Where(file => file.Zoom >= DbMetadataZoomThreshold)
                .Select(t => new { t.Id, t.TileFilePath, t.Size })
                .ToListAsync();

            if (!lruCache.Any()) return;

            // Collect file paths with IDs for Phase 2 size lookup.
            var fileInfo = lruCache
                .Select(t => (Id: t.Id, FilePath: t.TileFilePath, Size: (long)t.Size))
                .ToList();

            var totalFiles = fileInfo.Count;
            await BroadcastPurgeProgressAsync(sseService, sseChannel, "progress", "lru", 0, totalFiles);

            // Phase 1: Commit DB deletions first in chunks of 1000 IDs.
            // Chunking prevents PostgreSQL query plan explosion from large IN clauses.
            // Re-fetches entities by ID inside the retry lambda so each attempt starts
            // with a clean change tracker — prevents entity tracking conflicts on retry.
            // Captures actual sizes from re-fetched entities (not stale projected sizes)
            // so Phase 2's _currentCacheSize decrement is accurate.
            var lruIds = lruCache.Select(t => t.Id).ToList();
            var actualSizes = new Dictionary<int, long>();
            const int chunkSize = 1000;
            foreach (var chunk in lruIds.Chunk(chunkSize))
            {
                var chunkList = chunk.ToList();
                await RetryOperationAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();
                    var toDelete = await dbContext.TileCacheMetadata
                        .Where(t => chunkList.Contains(t.Id))
                        .ToListAsync();
                    if (toDelete.Any())
                    {
                        // Capture sizes before deletion — these reflect the current DB state.
                        foreach (var t in toDelete)
                            actualSizes[t.Id] = (long)t.Size;
                        dbContext.TileCacheMetadata.RemoveRange(toDelete);
                        await dbContext.SaveChangesAsync();
                    }
                }, 3, 1000);
            }

            _logger.LogInformation("LRU purge: {Count} DB records deleted.", lruCache.Count);

            // Phase 2: Delete files from disk (best-effort, after DB commit succeeded).
            // Chunked lock acquisition (10 files per lock) to minimize contention with
            // CacheTileAsync writes during large purge operations.
            // Uses actualSizes from re-fetched entities to minimize _currentCacheSize drift.
            const int deleteChunkSize = 10;
            var deletedFiles = 0;
            foreach (var chunk in fileInfo.Chunk(deleteChunkSize))
            {
                await _cacheLock.WaitAsync();
                try
                {
                    foreach (var (id, filePath, _) in chunk)
                    {
                        try
                        {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            if (actualSizes.TryGetValue(id, out var actualSize))
                            {
                                Interlocked.Add(ref _currentCacheSize, -actualSize);
                            }

                            TryRemoveHotMetadataEntryFromPath(filePath);
                        }
                    }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Error deleting LRU cache file {File}", filePath);
                        }
                    }
                }
                finally
                {
                    _cacheLock.Release();
                }

                // Yield after each chunk to give CacheTileAsync callers a chance to acquire
                // the lock, preventing writer starvation during large purge operations.
                await Task.Yield();

                deletedFiles += chunk.Length;
                await BroadcastPurgeProgressAsync(sseService, sseChannel, "progress", "lru",
                    deletedFiles, totalFiles);
            }

            TryClearHotMetadataCache();
        }
        finally
        {
            Interlocked.Exchange(ref _purgeInProgress, 0);
        }
    }

    /// <summary>
    /// Broadcasts a purge progress event via SSE if a service and channel are provided.
    /// Safe to call with null parameters (no-op).
    /// </summary>
    private async Task BroadcastPurgeProgressAsync(SseService? sseService, string? sseChannel,
        string eventType, string purgeType, int deletedFiles, int totalFiles,
        string? errorMessage = null)
    {
        if (sseService == null || sseChannel == null) return;

        var percent = totalFiles > 0 ? (int)((double)deletedFiles / totalFiles * 100) : 0;
        var payload = JsonSerializer.Serialize(new
        {
            eventType,
            purgeType,
            deletedFiles,
            totalFiles,
            percentComplete = percent,
            errorMessage
        });

        try
        {
            await sseService.BroadcastAsync(sseChannel, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast purge progress via SSE");
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
                TryRemoveHotMetadataEntryFromPath(tileFilePath);
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Updates LastAccessed at most once per throttle window for fresh hot-cache hits.
    /// </summary>
    private async Task TouchLastAccessedFromHotHitAsync(int zoom, int x, int y)
    {
        if (!_tileMetadataHotCache.TryBeginLastAccessedPersist(zoom, x, y))
        {
            return;
        }

        try
        {
            var meta = await _dbContext.TileCacheMetadata
                .FirstOrDefaultAsync(t => t.Zoom == zoom && t.X == x && t.Y == y);
            if (meta == null)
            {
                _tileMetadataHotCache.AbortLastAccessedPersist(zoom, x, y);
                TryRemoveHotMetadataEntry(zoom, x, y);
                return;
            }

            meta.LastAccessed = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            _tileMetadataHotCache.CompleteLastAccessedPersist(zoom, x, y);
        }
        catch (DbUpdateConcurrencyException)
        {
            _tileMetadataHotCache.AbortLastAccessedPersist(zoom, x, y);
            _logger.LogDebug(
                "LastAccessed update skipped due to concurrency after hot-cache hit (non-critical)");
        }
        catch
        {
            _tileMetadataHotCache.AbortLastAccessedPersist(zoom, x, y);
            throw;
        }
    }

    /// <summary>
    /// Best-effort hot metadata lookup that degrades to the DB path on cache failures.
    /// </summary>
    private bool TryGetHotMetadataEntry(int zoom, int x, int y, out HotTileMetadataCacheEntry? metadata)
    {
        try
        {
            return _tileMetadataHotCache.TryGet(GetTileMetadataHotCacheSizeMb(), zoom, x, y, out metadata);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tile metadata hot-cache lookup failed for z={Zoom} x={X} y={Y}", zoom, x, y);
            metadata = null;
            return false;
        }
    }

    /// <summary>
    /// Best-effort hot metadata insert/update after durable tile metadata changes succeed.
    /// </summary>
    private void TrySetHotMetadataEntry(int zoom, int x, int y, TileCacheMetadata metadata)
    {
        TrySetHotMetadataEntry(zoom, x, y, new HotTileMetadataCacheEntry
        {
            ExpiresAtUtc = metadata.ExpiresAtUtc,
            ETag = metadata.ETag,
            LastModifiedUpstream = metadata.LastModifiedUpstream
        });
    }

    /// <summary>
    /// Best-effort hot metadata insert/update after durable tile metadata changes succeed.
    /// </summary>
    private void TrySetHotMetadataEntry(int zoom, int x, int y, HotTileMetadataCacheEntry metadata)
    {
        try
        {
            _tileMetadataHotCache.Set(GetTileMetadataHotCacheSizeMb(), zoom, x, y, metadata);
            _tileMetadataHotCache.CompleteLastAccessedPersist(zoom, x, y);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tile metadata hot-cache update failed for z={Zoom} x={X} y={Y}", zoom, x, y);
        }
    }

    /// <summary>
    /// Best-effort hot metadata invalidation for an explicit tile delete path.
    /// </summary>
    private void TryRemoveHotMetadataEntry(int zoom, int x, int y)
    {
        try
        {
            _tileMetadataHotCache.Remove(zoom, x, y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tile metadata hot-cache removal failed for z={Zoom} x={X} y={Y}", zoom, x, y);
        }
    }

    /// <summary>
    /// Best-effort hot metadata invalidation for a cached file path with the standard z_x_y file name format.
    /// </summary>
    private void TryRemoveHotMetadataEntryFromPath(string tileFilePath)
    {
        var tileName = Path.GetFileNameWithoutExtension(tileFilePath)?.Split('_');
        if (tileName is not { Length: 3 } ||
            !int.TryParse(tileName[0], out var zoom) ||
            !int.TryParse(tileName[1], out var x) ||
            !int.TryParse(tileName[2], out var y))
        {
            return;
        }

        TryRemoveHotMetadataEntry(zoom, x, y);
    }

    /// <summary>
    /// Best-effort full hot metadata clear after purge/reset operations.
    /// </summary>
    private void TryClearHotMetadataCache()
    {
        try
        {
            _tileMetadataHotCache.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tile metadata hot-cache clear failed after purge/reset.");
        }
    }

    /// <summary>
    /// Reads the current admin-configured hot metadata cache budget.
    /// </summary>
    private int GetTileMetadataHotCacheSizeMb() => _applicationSettings.GetSettings().TileMetadataHotCacheSizeMB;

}
