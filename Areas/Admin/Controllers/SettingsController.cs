using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Authorize(Roles = "Admin")]
    [Area("Admin")]
    public class SettingsController : BaseController
    {
        private readonly IApplicationSettingsService _settingsService;
        private readonly TileCacheService _tileCacheService;

        public SettingsController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            IApplicationSettingsService settingsService, TileCacheService tileCacheService)
            : base(logger, dbContext)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _tileCacheService = tileCacheService ?? throw new ArgumentNullException(nameof(tileCacheService));
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ApplicationSettings settings = _settingsService.GetSettings();
            ViewData["CachePath"] = _tileCacheService.GetCacheDirectory();
            ViewData["TotalCacheFiles"] = await _tileCacheService.GetTotalCachedFilesAsync();
            ViewData["LruTotalFiles"] = await _tileCacheService.GetLruTotalFilesInDbAsync();
            double total = await _tileCacheService.GetCacheFileSizeInMbAsync();
            ViewData["TotalCacheSize"] = Math.Round(total, 2);
            ViewData["TotalCacheSizeGB"] = Math.Round(total / 1024, 3);
            double  lru = await _tileCacheService.GetLruCachedInMbFilesAsync();
            ViewData["TotalLru"] = Math.Round(lru, 2);
            ViewData["TotalLruGB"] = Math.Round(lru / 1024, 3);
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
                
                return Ok( new { success = true, message = "The map tile cache has been deleted successfully.", CacheStatus = cacheStatus });
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
                
                return Ok( new { success = true, message = "The map tile cache for zoom levels equal or greater of 9, has been deleted successfully.", CacheStatus = cacheStatus  });
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
            double  lru = await _tileCacheService.GetLruCachedInMbFilesAsync();
            
            cacheStatus.TotalCacheFiles = await _tileCacheService.GetTotalCachedFilesAsync();
            cacheStatus.LruTotalFiles = await _tileCacheService.GetLruTotalFilesInDbAsync();
            cacheStatus.TotalCacheSize =  Math.Round(total, 2);
            cacheStatus.TotalCacheSizeGB =  Math.Round(total / 1024, 3);
            cacheStatus.TotalLru =  Math.Round(lru, 2);
            cacheStatus.TotalLruGB =  Math.Round(lru / 1024, 3);
            
            return cacheStatus;
        }
    }
}