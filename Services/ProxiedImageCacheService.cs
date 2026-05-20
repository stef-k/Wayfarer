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
public class ProxiedImageCacheService : IProxiedImageCacheService
{
    private readonly ILogger<ProxiedImageCacheService> _logger;
    private readonly ApplicationDbContext _dbContext;
    private readonly IApplicationSettingsService _settingsService;
    private readonly string _cacheDirectory;

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

    public ProxiedImageCacheService(
        ILogger<ProxiedImageCacheService> logger,
        ApplicationDbContext dbContext,
        IApplicationSettingsService settingsService,
        IConfiguration configuration)
    {
        _logger = logger;
        _dbContext = dbContext;
        _settingsService = settingsService;

        // Read cache directory from configuration, fallback to default
        var configuredDir = configuration.GetSection("CacheSettings:ImageCacheDirectory").Value;
        if (string.IsNullOrEmpty(configuredDir))
        {
            _cacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), "ImageCache");
        }
        else
        {
            _cacheDirectory = Path.IsPathRooted(configuredDir)
                ? configuredDir
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredDir));
        }
    }

    /// <inheritdoc />
    public void Initialize()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
                _logger.LogInformation("ImageCache directory created at {CacheDirectory}.", _cacheDirectory);
            }

            InitializeCacheSizeFromDb();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Insufficient permissions to create ImageCache directory at {CacheDirectory}.",
                _cacheDirectory);
        }
    }

    /// <inheritdoc />
    public async Task<ProxiedImageCacheResult> GetAsync(string cacheKey)
    {
        var settings = _settingsService.GetSettings();

        // Caching disabled
        if (settings.MaxCacheImageSizeInMB < 0)
            return new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null);

        string? filePath;
        string? contentType;
        ProxiedImageCacheStatus status;

        // Lock-free DB read — scoped DbContext makes concurrent reads safe
        try
        {
            var metadata = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (metadata == null)
                return new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null);

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

            filePath = metadata.FilePath;
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
            if (!File.Exists(filePath))
            {
                // File missing on disk — clean up the DB entry (rare error path, uses lock)
                _logger.LogWarning("Image cache file missing for key {CacheKey}. Removing DB entry.", cacheKey);
                await _cacheLock.WaitAsync();
                try
                {
                    var staleMetadata = await _dbContext.ImageCacheMetadata
                        .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);
                    if (staleMetadata != null)
                    {
                        _dbContext.ImageCacheMetadata.Remove(staleMetadata);
                        Interlocked.Add(ref _currentCacheSize, -staleMetadata.Size);
                        await _dbContext.SaveChangesAsync();
                    }
                }
                finally
                {
                    _cacheLock.Release();
                }
                return new ProxiedImageCacheResult(ProxiedImageCacheStatus.DiskMissingOrError, null, null, filePath);
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

    /// <inheritdoc />
    public async Task SetAsync(string cacheKey, byte[] bytes, string contentType)
    {
        var settings = _settingsService.GetSettings();

        // Caching disabled
        if (settings.MaxCacheImageSizeInMB < 0)
            return;

        var filePath = Path.Combine(_cacheDirectory, $"{cacheKey}.dat");
        var tempFilePath = CreateTempImagePath(filePath);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllBytesAsync(tempFilePath, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing proxy image file for key {CacheKey}.", cacheKey);
            return;
        }

        // Lock only for DB operations and size counter mutations
        await _cacheLock.WaitAsync();
        try
        {
            // Check if entry already exists (race: another request cached it while we were downloading)
            var existing = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (existing != null)
            {
                ReplaceImageFileAtomically(tempFilePath, existing.FilePath);
                var sizeDelta = bytes.Length - existing.Size;
                existing.ContentType = contentType;
                existing.Size = bytes.Length;
                existing.CreatedAt = DateTime.UtcNow;
                existing.LastAccessed = DateTime.UtcNow;
                await SaveWithConcurrencyRetryAsync(existing);
                Interlocked.Add(ref _currentCacheSize, sizeDelta);
                return;
            }

            // Evict in a loop until enough space is available or no more entries remain
            var maxSizeBytes = settings.MaxCacheImageSizeInMB * 1024L * 1024L;
            while (Interlocked.Read(ref _currentCacheSize) + bytes.Length > maxSizeBytes)
            {
                var evictedCount = await EvictLruEntriesAsync();
                if (evictedCount == 0) break;
            }

            // Create DB metadata entry — wrap in try/catch to delete orphaned file on failure
            var metadata = new ImageCacheMetadata
            {
                CacheKey = cacheKey,
                ContentType = contentType,
                FilePath = filePath,
                Size = bytes.Length,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow
            };

            _dbContext.ImageCacheMetadata.Add(metadata);
            try
            {
                ReplaceImageFileAtomically(tempFilePath, filePath);
                await _dbContext.SaveChangesAsync();
                Interlocked.Add(ref _currentCacheSize, bytes.Length);
            }
            catch
            {
                // DB save failed — delete the orphaned file so it doesn't linger uncounted
                try { File.Delete(filePath); } catch { /* best-effort cleanup */ }
                try { File.Delete(tempFilePath); } catch { /* best-effort cleanup */ }
                throw;
            }

            _logger.LogInformation("Cached proxy image: key={CacheKey}, size={Size} bytes.", cacheKey, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching proxy image for key {CacheKey}.", cacheKey);
        }
        finally
        {
            TryDeleteTempImage(tempFilePath);
            _cacheLock.Release();
        }
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

        if (entriesToEvict.Count == 0)
            return 0;

        foreach (var entry in entriesToEvict)
        {
            _dbContext.ImageCacheMetadata.Remove(entry);
            Interlocked.Add(ref _currentCacheSize, -entry.Size);

            if (File.Exists(entry.FilePath))
            {
                try
                {
                    File.Delete(entry.FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cached image file {FilePath}.", entry.FilePath);
                }
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Evicted {Count} LRU image cache entries.", entriesToEvict.Count);
        return entriesToEvict.Count;
    }

    /// <summary>
    /// Saves metadata changes with retry on concurrency conflicts.
    /// Uses the same retry pattern as <see cref="TileCacheService"/>.
    /// </summary>
    private async Task SaveWithConcurrencyRetryAsync(ImageCacheMetadata metadata)
    {
        var attempts = 0;
        var updated = false;

        while (!updated && attempts < 3)
        {
            attempts++;
            try
            {
                _dbContext.ImageCacheMetadata.Update(metadata);
                await _dbContext.SaveChangesAsync();
                updated = true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var entry = ex.Entries.Single();
                var databaseValues = await entry.GetDatabaseValuesAsync();

                if (databaseValues == null)
                {
                    _logger.LogWarning("Image cache metadata was deleted by another process for key {CacheKey}.",
                        metadata.CacheKey);
                    return;
                }

                // Reload database values and reapply our LastAccessed update
                var dbMetadata = (ImageCacheMetadata)databaseValues.ToObject();
                metadata.LastAccessed = DateTime.UtcNow;
                entry.OriginalValues.SetValues(databaseValues);
            }
        }
    }

    /// <summary>
    /// Creates a same-directory temporary path for atomic image replacement.
    /// </summary>
    private static string CreateTempImagePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? ".";
        var fileName = Path.GetFileName(filePath);
        return Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Replaces the final image with a same-directory temp file so readers never see partial bytes.
    /// </summary>
    private static void ReplaceImageFileAtomically(string tempFilePath, string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Replace(tempFilePath, filePath, null);
            return;
        }

        File.Move(tempFilePath, filePath);
    }

    /// <summary>
    /// Deletes an unused temp file without masking the original write or replacement error.
    /// </summary>
    private static void TryDeleteTempImage(string tempFilePath)
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
}
