using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;

public class TileCacheService
{
    private readonly ILogger<TileCacheService> _logger;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IApplicationSettingsService _applicationSettings;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// How many tiles to delete from LRU cached storage when the limit has been reached.
    /// </summary>
    private const int LRU_TO_EVICT = 50;

    private readonly IServiceScopeFactory _serviceScopeFactory;

    // 1 GB maximum cache size for zoom levels >= 9.
    private readonly int _maxCacheSizeInMB;

    // Tracks the total size of cached tiles in Bytes (for zoom >= 9)
    private long _currentCacheSize = 0;

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
        }
        catch (UnauthorizedAccessException uae)
        {
            _logger.LogError(uae, "Insufficient permissions to create TileCache directory.");
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
                var response = await _httpClient.GetAsync(tileUrl);
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
                    response.StatusCode, tileUrl);
                retryCount--;
                if (retryCount == 0)
                {
                    _logger.LogError("Failed to download tile after multiple attempts: {TileUrl}", tileUrl);
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
                    // If adding a new tile would exceed the cache limit in Gibabytes, evict tiles.
                    if ((_currentCacheSize + (tileData?.Length ?? 0)) > (_maxCacheSizeInMB * 1024 * 1024))
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
                    _currentCacheSize += tileData?.Length ?? 0;
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
                    _currentCacheSize = _currentCacheSize - oldSize + (tileData?.Length ?? 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching tile from {TileUrl}.", tileUrl);
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
            _currentCacheSize -= tile.Size;

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
                    File.Delete(tileFilePath);
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
                _logger.LogInformation("Tile found in cache: {TileFilePath}", tileFilePath);
                // for zoom levels >= 9
                if (zoomLvl >= 9)
                {
                    await UpdateTileLastAccessedAsync(zoomLevel, xCoordinate, yCoordinate);
                }

                return await File.ReadAllBytesAsync(tileFilePath);
            }

            // 2. If the tile is not on disk, but we have a URL, attempt to fetch it.
            if (string.IsNullOrEmpty(tileUrl))
            {
                _logger.LogError("Tile URL is missing for retrieval attempt.");
                return null;
            }

            if (!string.IsNullOrEmpty(tileUrl))
            {
                _logger.LogInformation("Tile file not found. Attempting to fetch from: {TileUrl}", tileUrl);
                await CacheTileAsync(tileUrl, zoomLevel, xCoordinate, yCoordinate);

                // After fetching, check again.
                if (File.Exists(tileFilePath))
                {
                    // for zoom levels >= 9
                    if (zoomLvl >= 9)
                    {
                        await UpdateTileLastAccessedAsync(zoomLevel, xCoordinate, yCoordinate);
                    }

                    return await File.ReadAllBytesAsync(tileFilePath);
                }
                else
                {
                    _logger.LogWarning("Tile was not fetched successfully from {TileUrl}", tileUrl);
                }
            }
            else
            {
                _logger.LogWarning("Tile not found and no tileUrl was provided to fetch it: {TileFilePath}",
                    tileFilePath);
            }

            // 3. If we get here, the tile is not available.
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
    /// Gets the total tile LRU (Least Recently Used) cache size store in file system.
    /// LRU cache is cached tiles with zoom levels >= 9.
    /// </summary>
    /// <returns></returns>
    public Task<double> GetLruCachedInMbFilesAsync()
    {
        var lruSize = _dbContext.TileCacheMetadata.Sum(t => t.Size);

        if (lruSize <= 0)
        {
            return Task.FromResult(0.0);
        }

        return Task.FromResult(lruSize / 1024.0 / 1024.0);
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
                    File.Delete(file); // Delete the file from disk
                    _currentCacheSize -= fileSize; // Update cache size tracker

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
    /// Purges all LRU tile cache
    /// </summary>
    public async Task PurgeLRUCacheAsync()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        using var transaction = await dbContext.Database.BeginTransactionAsync(); // Explicit transaction

        var lruCache = await dbContext.TileCacheMetadata
            .Where(file => file.Zoom >= 9)
            .AsTracking()
            .ToListAsync();

        var filesToDelete = new List<TileCacheMetadata>();

        foreach (var file in lruCache)
        {
            try
            {
                if (File.Exists(file.TileFilePath))
                {
                    // Use RetryOperationAsync for file deletion logic
                    await RetryOperationAsync(() =>
                    {
                        File.Delete(file.TileFilePath);
                        _currentCacheSize -= file.Size;
                        filesToDelete.Add(file);
                        return Task.CompletedTask;
                    }, 3, 500); // 3 retries, 500ms delay between retries
                }
                else
                {
                    _logger.LogWarning("File not found for deletion: {File}", file.TileFilePath);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing file {File}", file.TileFilePath);
            }
        }

        if (filesToDelete.Any())
        {
            // Use RetryOperationAsync for database save logic
            await RetryOperationAsync(async () =>
            {
                dbContext.TileCacheMetadata.RemoveRange(filesToDelete);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync(); // Commit after saving
            }, 3, 1000); // 3 retries, 1000ms delay between retries
        }
        else
        {
            await transaction.RollbackAsync(); // Rollback if no files were deleted
        }
    }
}