using System.Net.Http.Headers;
using System.Net;
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

    /// <summary>
    /// Lock for serializing file system operations across all service instances.
    /// Static because TileCacheService is scoped (per-request) but file operations must be synchronized globally.
    /// </summary>
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// How many tiles to delete from LRU cached storage when the limit has been reached.
    /// </summary>
    private const int LRU_TO_EVICT = 50;

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
    private static bool _cacheSizeInitialized = false;

    /// <summary>
    /// Lock object for one-time cache size initialization.
    /// </summary>
    private static readonly object _initLock = new();

    public TileCacheService(ILogger<TileCacheService> logger, IConfiguration configuration, HttpClient httpClient,
        ApplicationDbContext dbContext, IApplicationSettingsService applicationSettings,
        IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _applicationSettings = applicationSettings;
        _serviceScopeFactory = serviceScopeFactory;
        _maxCacheSizeInMB = _applicationSettings.GetSettings().MaxCacheTileSizeInMB;

        if (_maxCacheSizeInMB <= 0)
        {
            _logger.LogWarning("Invalid MaxCacheTileSizeInMB value: {MaxCacheTileSizeInMB}. Defaulting to 1024 MB.",
                _maxCacheSizeInMB);
            _maxCacheSizeInMB = 1024; // Default to 1GB
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
            // interpret relative paths as “under current directory”
            _cacheDirectory = Path.IsPathRooted(_cacheDirectory)
                ? _cacheDirectory
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _cacheDirectory));
        }

        ConfigureHttpClient();
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

    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                                             "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                                             "Chrome/90.0.4430.93 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.8));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Clear();
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
    }

    /// <summary>
    /// Sends a tile request and applies a same-host redirect policy.
    /// </summary>
    private async Task<HttpResponseMessage?> SendTileRequestAsync(string tileUrl)
    {
        const int maxRedirects = 3;
        var initialUri = new Uri(tileUrl);
        var currentUri = initialUri;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
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

    private static bool IsRedirectStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.RedirectKeepVerb
            or HttpStatusCode.SeeOther
            or HttpStatusCode.PermanentRedirect;
    }

    /// <summary>
    /// Downloads a tile from the given URL and caches it on the file system.
    /// For zoom levels >= 9, metadata is stored (or updated) in the database.
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
                    await _cacheLock.WaitAsync();
                    try
                    {
                        if (!File.Exists(tileFilePath)) // Prevent overwriting existing files
                        {
                            await File.WriteAllBytesAsync(tileFilePath, tileData);
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
                        LastAccessed = DateTime.UtcNow
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
    /// Evicts the least recently used tiles (in batches) from the database and file system to free up cache space.
    /// </summary>
    private async Task EvictDbTilesAsync()
    {
        // Retrieve a batch of the least recently accessed tiles.
        var tilesToEvict = await _dbContext.TileCacheMetadata
            .OrderBy(t => t.LastAccessed)
            .Take(LRU_TO_EVICT) // Adjust the eviction batch size as needed.
            .ToListAsync();

        foreach (var tile in tilesToEvict)
        {
            _dbContext.TileCacheMetadata.Remove(tile);
            Interlocked.Add(ref _currentCacheSize, -tile.Size);

            // Remove the corresponding file.
            var tileFilePath = Path.Combine(_cacheDirectory, $"{tile.Zoom}_{tile.X}_{tile.Y}.png");
            if (!File.Exists(tileFilePath))
            {
                _logger.LogWarning("Tile file already deleted: {TileFilePath}", tileFilePath);
                continue;
            }

            try
            {
                if (File.Exists(tileFilePath))
                {
                    await _cacheLock.WaitAsync();
                    try
                    {
                        // Serialize file deletes with cache reads/writes.
                        File.Delete(tileFilePath);
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }
                    _logger.LogInformation("Tile file deleted: {TileFilePath}", tileFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tile file: {TileFilePath}", tileFilePath);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Evicted tiles to maintain cache size.");
    }


    /// <summary>
    /// Retrieves a tile from the cache. First checks the file system;
    /// if the file is missing but metadata exists, it attempts to re-fetch the tile.
    /// </summary>
    public async Task<byte[]?> RetrieveTileAsync(string zoomLevel, string xCoordinate, string yCoordinate,
        string? tileUrl = null)
    {
        try
        {
            var tileFileName = $"{zoomLevel}_{xCoordinate}_{yCoordinate}.png";
            var tileFilePath = Path.Combine(_cacheDirectory, tileFileName);
            var zoomLvl = int.Parse(zoomLevel);
            // 1. Check the file system first.
            if (File.Exists(tileFilePath))
            {
                _logger.LogDebug("Tile found in cache: {TileFilePath}", tileFilePath);
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

                if (cachedTileData != null)
                {
                    // for zoom levels >= 9
                    if (zoomLvl >= 9)
                    {
                        await UpdateTileLastAccessedAsync(zoomLevel, xCoordinate, yCoordinate);
                    }

                    return cachedTileData;
                }
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

            if (fetchedTileData != null)
            {
                // for zoom levels >= 9
                if (zoomLvl >= 9)
                {
                    await UpdateTileLastAccessedAsync(zoomLevel, xCoordinate, yCoordinate);
                }

                return fetchedTileData;
            }

            _logger.LogWarning("Tile fetch failed from {TileUrl}", TileProviderCatalog.RedactApiKey(tileUrl));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tile from cache.");
            return null;
        }
    }


    /// <summary>
    /// Updates the LastAccessed timestamp for a tile in the database.
    /// </summary>
    private async Task UpdateTileLastAccessedAsync(string zoomLevel, string xCoordinate, string yCoordinate)
    {
        var tileMetadata = await _dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t =>
                t.Zoom == int.Parse(zoomLevel) && t.X == int.Parse(xCoordinate) && t.Y == int.Parse(yCoordinate));

        if (tileMetadata != null)
        {
            tileMetadata.LastAccessed = DateTime.UtcNow;
            _dbContext.TileCacheMetadata.Update(tileMetadata);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Updated LastAccessed for tile at Zoom {Zoom}, X {X}, Y {Y}", zoomLevel, xCoordinate,
                yCoordinate);
        }
        else
        {
            _logger.LogWarning("Tile metadata not found in database for Zoom {Zoom}, X {X}, Y {Y}", zoomLevel,
                xCoordinate, yCoordinate);
        }
    }

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

    /// <summary>
    /// Purges all tile cache both static (zoom levels <= 8) and LRU cache (zoom levels >= 9)
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

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory))
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
