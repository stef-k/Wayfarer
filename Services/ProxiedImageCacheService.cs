using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>
/// Defines the contract for caching proxied and optimized external images on disk.
/// Provides disk-based caching with DB-tracked LRU eviction, following the same
/// pattern as <see cref="TileCacheService"/>.
/// </summary>
public interface IProxiedImageCacheService
{
    /// <summary>
    /// Returns cached image bytes and content type if a valid (non-expired) entry exists.
    /// Updates the LastAccessed timestamp on cache hits for LRU ordering.
    /// Returns null on cache miss or expired entry.
    /// </summary>
    Task<(byte[] Bytes, string ContentType)?> GetAsync(string cacheKey);

    /// <summary>
    /// Stores processed image bytes under the given cache key.
    /// Triggers LRU eviction if the cache size limit would be exceeded.
    /// </summary>
    Task SetAsync(string cacheKey, byte[] bytes, string contentType);

    /// <summary>
    /// Ensures the cache directory exists and initializes cache size tracking from the database.
    /// Called once at application startup.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Returns the total cached image size in megabytes (from DB metadata).
    /// </summary>
    Task<double> GetCacheSizeInMbAsync();

    /// <summary>
    /// Returns the total number of cached images (from DB metadata).
    /// </summary>
    Task<int> GetCachedImageCountAsync();
}

/// <summary>
/// Disk-based image proxy cache with DB-tracked LRU eviction.
/// Caches optimized images from the ProxyImage endpoint to avoid repeated
/// downloads and ImageSharp processing on every request.
/// Thread-safe via static SemaphoreSlim (same pattern as <see cref="TileCacheService"/>).
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
    private static bool _cacheSizeInitialized;

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
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(string cacheKey)
    {
        var settings = _settingsService.GetSettings();

        // Caching disabled
        if (settings.MaxCacheImageSizeInMB < 0)
            return null;

        await _cacheLock.WaitAsync();
        try
        {
            var metadata = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (metadata == null)
                return null;

            // Check time-based expiry
            var maxAge = TimeSpan.FromDays(settings.ImageCacheExpiryDays);
            if (DateTime.UtcNow - metadata.CreatedAt > maxAge)
            {
                _logger.LogInformation("Image cache entry expired for key {CacheKey}. Removing.", cacheKey);
                await RemoveEntryAsync(metadata);
                return null;
            }

            // Check file still exists on disk
            if (!File.Exists(metadata.FilePath))
            {
                _logger.LogWarning("Image cache file missing for key {CacheKey}. Removing DB entry.", cacheKey);
                _dbContext.ImageCacheMetadata.Remove(metadata);
                Interlocked.Add(ref _currentCacheSize, -metadata.Size);
                await _dbContext.SaveChangesAsync();
                return null;
            }

            // Read the file bytes
            var bytes = await File.ReadAllBytesAsync(metadata.FilePath);

            // Update LastAccessed for LRU ordering
            metadata.LastAccessed = DateTime.UtcNow;
            await SaveWithConcurrencyRetryAsync(metadata);

            return (bytes, metadata.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading image cache for key {CacheKey}.", cacheKey);
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string cacheKey, byte[] bytes, string contentType)
    {
        var settings = _settingsService.GetSettings();

        // Caching disabled
        if (settings.MaxCacheImageSizeInMB < 0)
            return;

        await _cacheLock.WaitAsync();
        try
        {
            // Check if entry already exists (race: another request cached it while we were downloading)
            var existing = await _dbContext.ImageCacheMetadata
                .FirstOrDefaultAsync(m => m.CacheKey == cacheKey);

            if (existing != null)
            {
                // Already cached — update LastAccessed and return
                existing.LastAccessed = DateTime.UtcNow;
                await SaveWithConcurrencyRetryAsync(existing);
                return;
            }

            // Check if adding this entry would exceed the cache size limit
            var maxSizeBytes = settings.MaxCacheImageSizeInMB * 1024L * 1024L;
            if (Interlocked.Read(ref _currentCacheSize) + bytes.Length > maxSizeBytes)
            {
                await EvictLruEntriesAsync();
            }

            // Write the file to disk
            var filePath = Path.Combine(_cacheDirectory, $"{cacheKey}.dat");
            await File.WriteAllBytesAsync(filePath, bytes);

            // Create DB metadata entry
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
            await _dbContext.SaveChangesAsync();
            Interlocked.Add(ref _currentCacheSize, bytes.Length);

            _logger.LogInformation("Cached proxy image: key={CacheKey}, size={Size} bytes.", cacheKey, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching proxy image for key {CacheKey}.", cacheKey);
        }
        finally
        {
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
    /// </summary>
    private async Task EvictLruEntriesAsync()
    {
        var entriesToEvict = await _dbContext.ImageCacheMetadata
            .OrderBy(m => m.LastAccessed)
            .Take(LruEvictionBatchSize)
            .ToListAsync();

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
    }

    /// <summary>
    /// Removes a single cache entry (file + DB metadata) and adjusts the size counter.
    /// </summary>
    private async Task RemoveEntryAsync(ImageCacheMetadata metadata)
    {
        _dbContext.ImageCacheMetadata.Remove(metadata);
        Interlocked.Add(ref _currentCacheSize, -metadata.Size);

        if (File.Exists(metadata.FilePath))
        {
            try
            {
                File.Delete(metadata.FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired image cache file {FilePath}.", metadata.FilePath);
            }
        }

        await _dbContext.SaveChangesAsync();
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
}
