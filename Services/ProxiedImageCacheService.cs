using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>
/// Disk-based image proxy cache with DB-tracked LRU eviction.
/// Caches optimized images from the ProxyImage endpoint to avoid repeated
/// downloads and ImageSharp processing on every request.
/// Read operations are lock-free for concurrent performance; writes and eviction
/// are serialized via static SemaphoreSlim (same pattern as <see cref="TileCacheService"/>).
/// </summary>
public partial class ProxiedImageCacheService : IProxiedImageCacheService
{
    private readonly ILogger<ProxiedImageCacheService> _logger;
    private readonly ApplicationDbContext _dbContext;
    private readonly IApplicationSettingsService _settingsService;
    private readonly ImageCacheStorage _storage;

    /// <summary>
    /// Number of images to evict per LRU batch when the cache size limit is exceeded.
    /// Matches <see cref="TileCacheService"/>'s eviction batch size.
    /// </summary>
    private const int LruEvictionBatchSize = 50;

    /// <summary>
    /// Minimum interval between LastAccessed updates on cache reads.
    /// Reduces DB writes on hot entries — concurrent updates both write "now" (harmless).
    /// </summary>
    private static readonly TimeSpan LastAccessedUpdateInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Lock for serializing file system and DB operations across all service instances.
    /// Static because the service is scoped (per-request) but cache operations must be synchronized globally.
    /// </summary>
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Tracks the total size of cached images in bytes.
    /// Static for cross-instance tracking, initialized from DB on startup.
    /// </summary>
    private static long _currentCacheSize;

    /// <summary>
    /// Whether the cache size has been initialized from the database.
    /// </summary>
    private static volatile bool _cacheSizeInitialized;

    /// <summary>
    /// Lock for one-time cache size initialization.
    /// </summary>
    private static readonly object _initLock = new();

    /// <summary>
    /// Test-overridable file replacement hook for deterministic write-failure coverage.
    /// </summary>
    private static Action<string, string> _replaceImageFile = ReplaceImageFileAtomicallyCore;

    /// <summary>
    /// Test-overridable metadata save hook for deterministic persistence-failure coverage.
    /// </summary>
    private static Func<ApplicationDbContext, Task<int>> _saveMetadataChanges =
        dbContext => dbContext.SaveChangesAsync();

    /// <summary>
    /// Test-only hook that runs after metadata is captured and before file existence is checked.
    /// </summary>
    private static Func<string, string, Task> _beforeFileReadForTesting = (_, _) => Task.CompletedTask;

    /// <summary>Uses the image authority for all persisted reference interpretation.</summary>
    public ProxiedImageCacheService(
        ILogger<ProxiedImageCacheService> logger,
        ApplicationDbContext dbContext,
        IApplicationSettingsService settingsService,
        ImageCacheStorage storage)
    {
        _logger = logger;
        _dbContext = dbContext;
        _settingsService = settingsService;

        _storage = storage;
    }

    /// <inheritdoc />
    public void Initialize()
    {
        try
        {
            if (!Directory.Exists(_storage.CurrentRoot))
            {
                Directory.CreateDirectory(_storage.CurrentRoot);
                _logger.LogInformation("ImageCache directory created at {CacheDirectory}.", _storage.CurrentRoot);
            }

            InitializeCacheSizeFromDb();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Insufficient permissions to create ImageCache directory at {CacheDirectory}.",
                _storage.CurrentRoot);
        }
    }

    /// <inheritdoc />
    public async Task<ProxiedImageCacheResult> GetAsync(string cacheKey)
    {
        var settings = _settingsService.GetSettings();

        // Caching disabled
        if (settings.MaxCacheImageSizeInMB < 0 || !ImageCacheStorage.IsCacheKey(cacheKey))
            return new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null);

        string? filePath;
        string capturedReference;
        string? contentType;
        ProxiedImageCacheStatus status;

        // Lock-free DB read — scoped DbContext makes concurrent reads safe
        try
        {
            var metadata = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (metadata == null)
                return new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null);

            if (_storage.Resolve(metadata) == null)
                return new ProxiedImageCacheResult(ProxiedImageCacheStatus.DiskMissingOrError, null, null, null);

            // Expired entries are stale-but-servable while the file remains present.
            // Expiry is the refresh cadence, not a user-facing delete trigger.
            var maxAge = TimeSpan.FromDays(settings.ImageCacheExpiryDays);
            status = DateTime.UtcNow - metadata.CreatedAt > maxAge
                ? ProxiedImageCacheStatus.StaleHit
                : ProxiedImageCacheStatus.FreshHit;

            // Conditional LastAccessed update — only when stale (>1 hour)
            // No lock needed; concurrent updates both write "now" (harmless)
            if (DateTime.UtcNow - metadata.LastAccessed > LastAccessedUpdateInterval)
            {
                try
                {
                    metadata.LastAccessed = DateTime.UtcNow;
                    await SaveWithConcurrencyRetryAsync(metadata);
                }
                catch (Exception ex)
                {
                    // Non-critical — log and continue serving the cached image
                    _logger.LogWarning(ex, "Failed to update LastAccessed for cache key {CacheKey}.", cacheKey);
                }
            }

            capturedReference = metadata.FilePath;
            filePath = _storage.Resolve(metadata);
            if (filePath == null)
                return new ProxiedImageCacheResult(ProxiedImageCacheStatus.DiskMissingOrError, null, null, null);
            contentType = metadata.ContentType;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading image cache for key {CacheKey}.", cacheKey);
            return new ProxiedImageCacheResult(ProxiedImageCacheStatus.DiskMissingOrError, null, null, null);
        }

        // File I/O outside the lock — unique filename per cache key prevents conflicts.
        // If the file was evicted between the DB check and this read, return null (cache miss).
        try
        {
            await _beforeFileReadForTesting(cacheKey, filePath);

            if (!File.Exists(filePath))
            {
                return await HandleMissingCapturedFileAsync(cacheKey, capturedReference, settings);
            }

            var bytes = await File.ReadAllBytesAsync(filePath);
            return new ProxiedImageCacheResult(status, bytes, contentType!, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading cached image file for key {CacheKey}.", cacheKey);
            return new ProxiedImageCacheResult(ProxiedImageCacheStatus.DiskMissingOrError, null, null, filePath);
        }
    }

    /// <summary>
    /// Cleans up a missing file only when the current metadata still points at that same file.
    /// </summary>
    private async Task<ProxiedImageCacheResult> HandleMissingCapturedFileAsync(
        string cacheKey,
        string capturedReference,
        ApplicationSettings settings)
    {
        await _cacheLock.WaitAsync();
        try
        {
            var currentMetadata = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (currentMetadata != null)
            {
                await _dbContext.Entry(currentMetadata).ReloadAsync();
            }

            if (currentMetadata == null || _dbContext.Entry(currentMetadata).State == EntityState.Detached)
            {
                return new ProxiedImageCacheResult(
                    ProxiedImageCacheStatus.DiskMissingOrError,
                    null,
                    null,
                    null);
            }

            if (!string.Equals(currentMetadata.FilePath, capturedReference, StringComparison.Ordinal))
            {
                return await ReadConcurrentRefreshFileAsync(currentMetadata, settings);
            }

            _logger.LogWarning("Image cache file missing for key {CacheKey}. Removing DB entry.", cacheKey);
            _dbContext.ImageCacheMetadata.Remove(currentMetadata);
            try
            {
                await SaveMetadataChangesAsync();
            }
            catch
            {
                _dbContext.Entry(currentMetadata).State = EntityState.Unchanged;
                throw;
            }
            Interlocked.Add(ref _currentCacheSize, -currentMetadata.Size);

            return new ProxiedImageCacheResult(
                ProxiedImageCacheStatus.DiskMissingOrError,
                null,
                null,
                null);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Reads the current file when a concurrent refresh moved metadata away from the captured path.
    /// </summary>
    private async Task<ProxiedImageCacheResult> ReadConcurrentRefreshFileAsync(
        ImageCacheMetadata metadata,
        ApplicationSettings settings)
    {
        var path = _storage.Resolve(metadata);
        if (path == null || !File.Exists(path))
        {
            return new ProxiedImageCacheResult(
                ProxiedImageCacheStatus.DiskMissingOrError,
                null,
                null,
                path);
        }

        var maxAge = TimeSpan.FromDays(settings.ImageCacheExpiryDays);
        var status = DateTime.UtcNow - metadata.CreatedAt > maxAge
            ? ProxiedImageCacheStatus.StaleHit
            : ProxiedImageCacheStatus.FreshHit;
        var bytes = await File.ReadAllBytesAsync(path);
        return new ProxiedImageCacheResult(status, bytes, metadata.ContentType, path);
    }

    /// <inheritdoc />
    public async Task<double> GetCacheSizeInMbAsync()
    {
        var totalSize = await _dbContext.ImageCacheMetadata.SumAsync(m => (long)m.Size);
        return totalSize <= 0 ? 0.0 : totalSize / 1024.0 / 1024.0;
    }

    /// <inheritdoc />
    public async Task<int> GetCachedImageCountAsync()
    {
        return await _dbContext.ImageCacheMetadata.CountAsync();
    }

    /// <summary>
    /// Initializes _currentCacheSize from the database on first access.
    /// Uses double-checked locking for thread-safe one-time initialization.
    /// </summary>
    private void InitializeCacheSizeFromDb()
    {
        if (_cacheSizeInitialized) return;

        lock (_initLock)
        {
            if (_cacheSizeInitialized) return;

            try
            {
                var totalSize = _dbContext.ImageCacheMetadata.Sum(m => (long)m.Size);
                Interlocked.Exchange(ref _currentCacheSize, totalSize);
                _cacheSizeInitialized = true;
                _logger.LogInformation("Initialized image cache size from database: {SizeInMB:F2} MB",
                    totalSize / 1024.0 / 1024.0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize image cache size from database. Starting with 0.");
                _cacheSizeInitialized = true;
            }
        }
    }

    /// <summary>
    /// Evicts the least recently accessed images in batches to free up cache space.
    /// Deletes both disk files and DB metadata entries.
    /// Returns the number of entries evicted (0 when no entries remain).
    /// </summary>
    private async Task<int> EvictLruEntriesAsync()
    {
        var entriesToEvict = await _dbContext.ImageCacheMetadata
            .OrderBy(m => m.LastAccessed)
            .Take(LruEvictionBatchSize)
            .ToListAsync();

        // Scoped contexts may already track older snapshots; reload intended rows before retirement.
        foreach (var entry in entriesToEvict)
            await _dbContext.Entry(entry).ReloadAsync();
        entriesToEvict.RemoveAll(entry => _dbContext.Entry(entry).State == EntityState.Detached);
        if (entriesToEvict.Count == 0)
            return 0;

        // Commit retirement before changing accounting or deleting any referenced bytes.
        var candidates = entriesToEvict.Select(entry => (Entry: entry, Path: _storage.Resolve(entry))).ToList();
        _dbContext.ImageCacheMetadata.RemoveRange(entriesToEvict);
        try
        {
            await SaveMetadataChangesAsync();
        }
        catch
        {
            foreach (var entry in entriesToEvict) _dbContext.Entry(entry).State = EntityState.Unchanged;
            throw;
        }
        Interlocked.Add(ref _currentCacheSize, -entriesToEvict.Sum(entry => (long)entry.Size));
        foreach (var candidate in candidates)
        {
            if (candidate.Path == null) continue;
            try
            {
                File.Delete(candidate.Path);
            }
            catch (Exception)
            {
                _logger.LogWarning("Failed to delete retired image for key {CacheKey}.", candidate.Entry.CacheKey);
            }
        }

        _logger.LogInformation("Evicted {Count} LRU image cache entries.", entriesToEvict.Count);
        return entriesToEvict.Count;
    }

}
