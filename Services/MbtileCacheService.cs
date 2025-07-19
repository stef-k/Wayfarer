using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;
using Wayfarer.Services.Helpers; // ← needed for RegionSourceResolver

public class MbtileCacheService
{
    private readonly ILogger<MbtileCacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly int _maxCacheSizeInMB;
    private readonly IApplicationSettingsService _applicationSettings;
    private readonly HttpClient _httpClient;
    private readonly RoutingCacheService _routingService;

    public MbtileCacheService(ILogger<MbtileCacheService> logger, IConfiguration configuration,
        IApplicationSettingsService applicationSettings, RoutingCacheService routingService)
    {
        _logger = logger;
        _applicationSettings = applicationSettings;
        _maxCacheSizeInMB = _applicationSettings.GetSettings().MaxCacheMbtilesSizeInMB;

        _cacheDirectory = configuration.GetSection("CacheSettings:MbtileCacheDirectory").Value;
        _httpClient = new HttpClient();
        _routingService = routingService;

        if (string.IsNullOrEmpty(_cacheDirectory))
        {
            _logger.LogWarning("MbtileCacheDirectory not set in configuration. Using default path.");
            _cacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), "MbtileCache");
        }
        else if (!Path.IsPathRooted(_cacheDirectory))
        {
            _cacheDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _cacheDirectory));
        }

        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
            _logger.LogInformation("Created MbtileCache directory at {Path}", _cacheDirectory);
        }
    }

    public double GetCurrentCacheSizeMB()
    {
        var files = Directory.GetFiles(_cacheDirectory, "*.mbtiles");
        long totalBytes = files.Sum(file => new FileInfo(file).Length);
        return totalBytes / 1024.0 / 1024.0;
    }

    public List<(string FileName, double SizeMB)> ListTiles()
    {
        return Directory.GetFiles(_cacheDirectory, "*.mbtiles")
            .Select(path => (Path.GetFileName(path), new FileInfo(path).Length / 1024.0 / 1024.0))
            .ToList();
    }

    public bool DeleteTile(string fileName)
    {
        var path = Path.Combine(_cacheDirectory, fileName);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            _logger.LogInformation("Deleted MBTiles file: {FileName}", fileName);
            var region = Path.GetFileNameWithoutExtension(fileName);
            _routingService.DeleteRoutingFile(region);
            _routingService.DeletePbfFile(region);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete tile: {FileName}", fileName);
            return false;
        }
    }

    public void ClearCache()
    {
        foreach (var path in Directory.GetFiles(_cacheDirectory, "*.mbtiles"))
        {
            try
            {
                File.Delete(path);
                var region = Path.GetFileNameWithoutExtension(path);
                _routingService.DeleteRoutingFile(region);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete {File}", path);
            }
        }
    }

    public string? GetTilePath(string fileName)
    {
        var path = Path.Combine(_cacheDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    public string GetCacheDirectory()
    {
        return GetCacheDirectoryPath();
    }

    public async Task<bool> DownloadAndCacheRemoteTileAsync(string region)
    {
        var mbtilesUrl = RegionSourceResolver.GetMbtilesUrl(region);
        var destPath = Path.Combine(_cacheDirectory, region + ".mbtiles");
        if (File.Exists(destPath)) return true;

        try
        {
            _logger.LogInformation("Fetching remote tile for region {Region} from {Url}", region, mbtilesUrl);
            var bytes = await _httpClient.GetByteArrayAsync(mbtilesUrl);
            EnsureAvailableSpace(bytes.Length);
            await File.WriteAllBytesAsync(destPath, bytes);
            _logger.LogInformation("Downloaded and cached MBTile for region: {Region}", region);
            _routingService.GenerateRoutingFile(region);
            RefreshManifest();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch MBTile for region: {Region}", region);
            return false;
        }
    }

    public string GetCacheDirectoryPath() => _cacheDirectory;

    public int GetMaxCacheSizeMB() => _maxCacheSizeInMB;

    public void RefreshManifest()
    {
        var files = Directory.GetFiles(_cacheDirectory, "*.mbtiles");

        var manifest = files.Select(path =>
        {
            var fileInfo = new FileInfo(path);
            return new
            {
                region = Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                name = Path.GetFileNameWithoutExtension(path),
                sizeMB = Math.Round(fileInfo.Length / 1024.0 / 1024.0, 2),
                minZoom = 0,
                maxZoom = 14,
                url = $"/api/mbtiles/{fileInfo.Name}"
            };
        });

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var manifestPath = Path.Combine(_cacheDirectory, "tiles.json");
        File.WriteAllText(manifestPath, json);
        _logger.LogInformation("tiles.json manifest written with {Count} entries", files.Length);
    }

    public void EnsureAvailableSpace(long bytesNeeded)
    {
        var files = Directory.GetFiles(_cacheDirectory, "*.mbtiles")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastAccessTimeUtc)
            .ToList();

        long totalBytes = files.Sum(f => f.Length);
        long maxBytes = _maxCacheSizeInMB * 1024L * 1024L;

        if (totalBytes + bytesNeeded <= maxBytes)
        {
            RefreshManifest();
            return;
        }

        _logger.LogInformation("Evicting MBTiles: current={0}MB, needed={1}MB, max={2}MB", totalBytes / 1024 / 1024,
            bytesNeeded / 1024 / 1024, maxBytes / 1024 / 1024);

        foreach (var file in files)
        {
            try
            {
                file.Delete();
                totalBytes -= file.Length;
                _logger.LogInformation("Evicted: {0}", file.Name);
                var region = Path.GetFileNameWithoutExtension(file.Name);
                _routingService.DeleteRoutingFile(region);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete {0}", file.FullName);
            }

            if (totalBytes + bytesNeeded <= maxBytes) break;
        }
    }
}