using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Authorize(Roles = "Admin")]
    [Area("Admin")]
    public class SettingsController : BaseController
    {
        private readonly IApplicationSettingsService _settingsService;
        private readonly TileCacheService _tileCacheService;
        private readonly MbtileCacheService _mbtileService;
        private readonly IWebHostEnvironment _env;

        public SettingsController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            IApplicationSettingsService settingsService,
            TileCacheService tileCacheService,
            MbtileCacheService mbtileService, IWebHostEnvironment env)
            : base(logger, dbContext)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _tileCacheService = tileCacheService ?? throw new ArgumentNullException(nameof(tileCacheService));
            _mbtileService = mbtileService ?? throw new ArgumentNullException(nameof(mbtileService));
            _env = env ?? throw new ArgumentNullException(nameof(env));
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ApplicationSettings settings = _settingsService.GetSettings();
            string uploadsPath = Path.Combine(_env.ContentRootPath, "Uploads", "Temp");
            ViewData["UploadsPath"] = uploadsPath;

            long totalUploadBytes = 0;
            int uploadFileCount = 0;

            if (Directory.Exists(uploadsPath))
            {
                var uploadFiles = new DirectoryInfo(uploadsPath).GetFiles("*", SearchOption.AllDirectories);
                totalUploadBytes = uploadFiles.Sum(file => file.Length);
                uploadFileCount = uploadFiles.Length;
            }

            // Fallbacks for unset values
            if (settings.MaxCacheTileSizeInMB == 0)
            {
                settings.MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB;
            }

            if (settings.MaxCacheMbtilesSizeInMB == 0)
            {
                settings.MaxCacheMbtilesSizeInMB = ApplicationSettings.DefaultMaxCacheMbtilesSizeInMB;
            }

            if (settings.UploadSizeLimitMB == 0)
            {
                settings.UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB;
            }

            // MBTiles
            string mbtilesPath = _mbtileService.GetCacheDirectory();
            ViewData["MbtilesPath"] = mbtilesPath;

            long totalMbtilesBytes = 0;
            int mbtilesFileCount = 0;

            if (Directory.Exists(mbtilesPath))
            {
                var mbtilesFiles = new DirectoryInfo(mbtilesPath).GetFiles("*.mbtiles", SearchOption.AllDirectories);
                totalMbtilesBytes = mbtilesFiles.Sum(f => f.Length);
                mbtilesFileCount = mbtilesFiles.Length;
            }

            ViewData["MbtilesFileCount"] = mbtilesFileCount;
            
            // Routing and PBF files
            // Routing + PBF cache paths
            string routingPath = Path.Combine(Directory.GetCurrentDirectory(), "RoutingCache");
            string pbfPath     = Path.Combine(Directory.GetCurrentDirectory(), "OsmPbfCache");

            // Safe helpers
            double GetFolderSizeMB(string path, string searchPattern) =>
                Directory.Exists(path)
                    ? new DirectoryInfo(path).GetFiles(searchPattern, SearchOption.TopDirectoryOnly).Sum(f => f.Length) / 1024.0 / 1024.0
                    : 0;

            int GetFileCount(string path, string searchPattern) =>
                Directory.Exists(path)
                    ? new DirectoryInfo(path).GetFiles(searchPattern, SearchOption.TopDirectoryOnly).Length
                    : 0;

            // Stats
            ViewData["RoutingPath"]     = routingPath;
            ViewData["RoutingSizeMB"]   = Math.Round(GetFolderSizeMB(routingPath, "*.routing"), 2);
            ViewData["RoutingFileCount"] = GetFileCount(routingPath, "*.routing");

            ViewData["PbfPath"]         = pbfPath;
            ViewData["PbfSizeMB"]       = Math.Round(GetFolderSizeMB(pbfPath, "*.osm.pbf"), 2);
            ViewData["PbfFileCount"]    = GetFileCount(pbfPath, "*.osm.pbf");
            
            double routingSize = Convert.ToDouble(ViewData["RoutingSizeMB"]);
            double pbfSize = Convert.ToDouble(ViewData["PbfSizeMB"]);
            double routingPbfTotalMB = routingSize + pbfSize;
            double routingPbfTotalGB = routingPbfTotalMB / 1024.0;

            ViewData["RoutingPbfTotalMB"] = Math.Round(routingPbfTotalMB, 2);
            ViewData["RoutingPbfTotalGB"] = Math.Round(routingPbfTotalGB, 3);

            // Tile Cache
            ViewData["CachePath"] = _tileCacheService.GetCacheDirectory();
            ViewData["TotalCacheFiles"] = await _tileCacheService.GetTotalCachedFilesAsync();
            ViewData["LruTotalFiles"] = await _tileCacheService.GetLruTotalFilesInDbAsync();
            double tileCacheSizeMB = await _tileCacheService.GetCacheFileSizeInMbAsync();
            double lruCacheSizeMB = await _tileCacheService.GetLruCachedInMbFilesAsync();

            ViewData["TotalCacheSize"] = Math.Round(tileCacheSizeMB, 2);
            ViewData["TotalCacheSizeGB"] = Math.Round(tileCacheSizeMB / 1024, 3);
            ViewData["TotalLru"] = Math.Round(lruCacheSizeMB, 2);
            ViewData["TotalLruGB"] = Math.Round(lruCacheSizeMB / 1024, 3);

            double mbtilesSizeMB = _mbtileService.GetCurrentCacheSizeMB();
            double mbtilesSizeGB = mbtilesSizeMB / 1024.0;
            ViewData["MbtilesCacheSizeMB"] = Math.Round(mbtilesSizeMB, 2);
            ViewData["MbtilesCacheSizeGB"] = Math.Round(mbtilesSizeGB, 2);

            double uploadsSizeMB = totalUploadBytes / (1024.0 * 1024.0);
            double uploadsSizeGB = uploadsSizeMB / 1024.0;
            ViewData["UploadsSizeMB"] = Math.Round(uploadsSizeMB, 2);
            ViewData["UploadsSizeGB"] = Math.Round(uploadsSizeGB, 3);
            ViewData["UploadsFileCount"] = uploadFileCount;

            // Combined
            double combinedTotalMB = tileCacheSizeMB + mbtilesSizeMB + uploadsSizeMB + routingPbfTotalMB;
            double combinedTotalGB = combinedTotalMB / 1024.0;
            ViewData["CombinedStorageMB"] = Math.Round(combinedTotalMB, 2);
            ViewData["CombinedStorageGB"] = Math.Round(combinedTotalGB, 3);

            SetPageTitle("Application Settings");
            return View(settings);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(ApplicationSettings updatedSettings)
        {
            if (!ValidateModelState())
            {
                return View("Index", updatedSettings);
            }

            try
            {
                ApplicationSettings? currentSettings = _dbContext.ApplicationSettings.Find(1);
                if (currentSettings != null)
                {
                    currentSettings.IsRegistrationOpen = updatedSettings.IsRegistrationOpen;
                    currentSettings.LocationTimeThresholdMinutes = updatedSettings.LocationTimeThresholdMinutes;
                    currentSettings.LocationDistanceThresholdMeters = updatedSettings.LocationDistanceThresholdMeters;
                    currentSettings.MaxCacheTileSizeInMB = updatedSettings.MaxCacheTileSizeInMB;
                    currentSettings.UploadSizeLimitMB = updatedSettings.UploadSizeLimitMB;
                    currentSettings.MaxCacheMbtilesSizeInMB = updatedSettings.MaxCacheMbtilesSizeInMB;
                    await _dbContext.SaveChangesAsync();
                }

                _settingsService.RefreshSettings();

                SetAlert("Settings updated and refreshed successfully.", "success");
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View("Index", updatedSettings);
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteAllMapTileCache()
        {
            try
            {
                await _tileCacheService.PurgeAllCacheAsync();

                var cacheStatus = await GetCacheStatus();

                return Ok(new
                {
                    success = true, message = "The map tile cache has been deleted successfully.",
                    CacheStatus = cacheStatus
                });
            }
            catch (Exception e)
            {
                return Ok(new { success = false, message = e.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteLruCache()
        {
            try
            {
                await _tileCacheService.PurgeLRUCacheAsync();

                var cacheStatus = await GetCacheStatus();

                return Ok(new
                {
                    success = true,
                    message =
                        "The map tile cache for zoom levels equal or greater of 9, has been deleted successfully.",
                    CacheStatus = cacheStatus
                });
            }
            catch (Exception e)
            {
                return Ok(new { success = false, message = e.Message });
            }
        }

        private class CacheStatus
        {
            public int TotalCacheFiles { get; set; }
            public int LruTotalFiles { get; set; }
            public double TotalCacheSize { get; set; }
            public double TotalCacheSizeGB { get; set; }
            public double TotalLru { get; set; }
            public double TotalLruGB { get; set; }
        }

        private async Task<CacheStatus> GetCacheStatus()
        {
            var cacheStatus = new CacheStatus();
            double total = await _tileCacheService.GetCacheFileSizeInMbAsync();
            double lru = await _tileCacheService.GetLruCachedInMbFilesAsync();

            cacheStatus.TotalCacheFiles = await _tileCacheService.GetTotalCachedFilesAsync();
            cacheStatus.LruTotalFiles = await _tileCacheService.GetLruTotalFilesInDbAsync();
            cacheStatus.TotalCacheSize = Math.Round(total, 2);
            cacheStatus.TotalCacheSizeGB = Math.Round(total / 1024, 3);
            cacheStatus.TotalLru = Math.Round(lru, 2);
            cacheStatus.TotalLruGB = Math.Round(lru / 1024, 3);

            return cacheStatus;
        }

        /// <summary>
        /// Clears the mbtiles cache
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ClearMbtilesCache()
        {
            _mbtileService.ClearCache();
            TempData["Message"] = "All MBTiles for mobile map cache cleared.";
            return RedirectToAction("Index");
        }
    }
}