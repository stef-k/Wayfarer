using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Authorize(Roles = "Admin")]
    [Area("Admin")]
    public class SettingsController : BaseController
    {
        private readonly IApplicationSettingsService _settingsService;
        private readonly TileCacheService _tileCacheService;
        private readonly IWebHostEnvironment _env;
        private readonly IServiceScopeFactory _scopeFactory;

        public SettingsController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            IApplicationSettingsService settingsService,
            TileCacheService tileCacheService,
            IWebHostEnvironment env,
            IServiceScopeFactory scopeFactory)
            : base(logger, dbContext)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _tileCacheService = tileCacheService ?? throw new ArgumentNullException(nameof(tileCacheService));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
            
            if (settings.UploadSizeLimitMB == 0)
            {
                settings.UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB;
            }
            
            // Removed Routing/PBF stats (Itinero cleanup)

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
            
            double uploadsSizeMB = totalUploadBytes / (1024.0 * 1024.0);
            double uploadsSizeGB = uploadsSizeMB / 1024.0;
            ViewData["UploadsSizeMB"] = Math.Round(uploadsSizeMB, 2);
            ViewData["UploadsSizeGB"] = Math.Round(uploadsSizeGB, 3);
            ViewData["UploadsFileCount"] = uploadFileCount;

            // Combined
            double combinedTotalMB = tileCacheSizeMB  + uploadsSizeMB;
            double combinedTotalGB = combinedTotalMB / 1024.0;
            ViewData["CombinedStorageMB"] = Math.Round(combinedTotalMB, 2);
            ViewData["CombinedStorageGB"] = Math.Round(combinedTotalGB, 3);

            // Tile provider presets for admin UI.
            SetTileProviderViewData();

            SetPageTitle("Application Settings");
            return View(settings);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(ApplicationSettings updatedSettings)
        {
            ApplicationSettings? currentSettings = _dbContext.ApplicationSettings.Find(1);
            if (currentSettings == null)
            {
                ModelState.AddModelError(string.Empty, "Application settings could not be loaded.");
            }

            // Cross-field validation for Trip Place Auto-Visited settings
            if (updatedSettings.VisitedMaxRadiusMeters < updatedSettings.VisitedMinRadiusMeters)
            {
                ModelState.AddModelError("VisitedMaxRadiusMeters",
                    "Max radius must be greater than or equal to min radius.");
            }

            if (updatedSettings.VisitedMaxSearchRadiusMeters < updatedSettings.VisitedMaxRadiusMeters)
            {
                ModelState.AddModelError("VisitedMaxSearchRadiusMeters",
                    "Search radius must be greater than or equal to max radius.");
            }

            if (currentSettings != null)
            {
                // Validate tile provider settings before model validation.
                NormalizeTileProviderSettings(currentSettings, updatedSettings);
            }

            if (!ValidateModelState())
            {
                SetTileProviderViewData();
                return View("Index", updatedSettings);
            }

            try
            {
                if (currentSettings != null)
                {
                    // Track changes for auditing
                    var changes = new List<string>();
                    void Track<T>(string name, T oldVal, T newVal)
                    {
                        if (!EqualityComparer<T>.Default.Equals(oldVal, newVal))
                            changes.Add($"{name}: {oldVal} -> {newVal}");
                    }

                    Track("IsRegistrationOpen", currentSettings.IsRegistrationOpen, updatedSettings.IsRegistrationOpen);
                    Track("LocationTimeThresholdMinutes", currentSettings.LocationTimeThresholdMinutes, updatedSettings.LocationTimeThresholdMinutes);
                    Track("LocationDistanceThresholdMeters", currentSettings.LocationDistanceThresholdMeters, updatedSettings.LocationDistanceThresholdMeters);
                    Track("LocationAccuracyThresholdMeters", currentSettings.LocationAccuracyThresholdMeters, updatedSettings.LocationAccuracyThresholdMeters);
                    Track("MaxCacheTileSizeInMB", currentSettings.MaxCacheTileSizeInMB, updatedSettings.MaxCacheTileSizeInMB);
                    Track("UploadSizeLimitMB", currentSettings.UploadSizeLimitMB, updatedSettings.UploadSizeLimitMB);
                    Track("TileProviderKey", currentSettings.TileProviderKey, updatedSettings.TileProviderKey);
                    Track("TileProviderUrlTemplate", currentSettings.TileProviderUrlTemplate, updatedSettings.TileProviderUrlTemplate);
                    Track("TileProviderAttribution", currentSettings.TileProviderAttribution, updatedSettings.TileProviderAttribution);
                    if (!string.Equals(currentSettings.TileProviderApiKey, updatedSettings.TileProviderApiKey, StringComparison.Ordinal))
                    {
                        changes.Add("TileProviderApiKey: [updated]");
                    }

                    // Trip Place Auto-Visited settings
                    Track("VisitedRequiredHits", currentSettings.VisitedRequiredHits, updatedSettings.VisitedRequiredHits);
                    Track("VisitedMinRadiusMeters", currentSettings.VisitedMinRadiusMeters, updatedSettings.VisitedMinRadiusMeters);
                    Track("VisitedMaxRadiusMeters", currentSettings.VisitedMaxRadiusMeters, updatedSettings.VisitedMaxRadiusMeters);
                    Track("VisitedAccuracyMultiplier", currentSettings.VisitedAccuracyMultiplier, updatedSettings.VisitedAccuracyMultiplier);
                    Track("VisitedAccuracyRejectMeters", currentSettings.VisitedAccuracyRejectMeters, updatedSettings.VisitedAccuracyRejectMeters);
                    Track("VisitedMaxSearchRadiusMeters", currentSettings.VisitedMaxSearchRadiusMeters, updatedSettings.VisitedMaxSearchRadiusMeters);
                    Track("VisitedPlaceNotesSnapshotMaxHtmlChars", currentSettings.VisitedPlaceNotesSnapshotMaxHtmlChars, updatedSettings.VisitedPlaceNotesSnapshotMaxHtmlChars);

                    // Treat empty stored values as defaults to avoid purging on upgrade.
                    var currentProviderKey = string.IsNullOrWhiteSpace(currentSettings.TileProviderKey)
                        ? ApplicationSettings.DefaultTileProviderKey
                        : currentSettings.TileProviderKey;
                    var currentProviderTemplate = string.IsNullOrWhiteSpace(currentSettings.TileProviderUrlTemplate)
                        ? ApplicationSettings.DefaultTileProviderUrlTemplate
                        : currentSettings.TileProviderUrlTemplate;
                    var currentProviderApiKey = string.IsNullOrWhiteSpace(currentSettings.TileProviderApiKey)
                        ? null
                        : currentSettings.TileProviderApiKey;

                    var shouldPurgeTileCache =
                        !string.Equals(currentProviderKey, updatedSettings.TileProviderKey, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(currentProviderTemplate, updatedSettings.TileProviderUrlTemplate, StringComparison.Ordinal) ||
                        !string.Equals(currentProviderApiKey, updatedSettings.TileProviderApiKey, StringComparison.Ordinal);

                    currentSettings.IsRegistrationOpen = updatedSettings.IsRegistrationOpen;
                    currentSettings.LocationTimeThresholdMinutes = updatedSettings.LocationTimeThresholdMinutes;
                    currentSettings.LocationDistanceThresholdMeters = updatedSettings.LocationDistanceThresholdMeters;
                    currentSettings.LocationAccuracyThresholdMeters = updatedSettings.LocationAccuracyThresholdMeters;
                    currentSettings.MaxCacheTileSizeInMB = updatedSettings.MaxCacheTileSizeInMB;
                    currentSettings.UploadSizeLimitMB = updatedSettings.UploadSizeLimitMB;
                    currentSettings.TileProviderKey = updatedSettings.TileProviderKey;
                    currentSettings.TileProviderUrlTemplate = updatedSettings.TileProviderUrlTemplate;
                    currentSettings.TileProviderAttribution = updatedSettings.TileProviderAttribution;
                    currentSettings.TileProviderApiKey = updatedSettings.TileProviderApiKey;

                    // Trip Place Auto-Visited settings
                    currentSettings.VisitedRequiredHits = updatedSettings.VisitedRequiredHits;
                    currentSettings.VisitedMinRadiusMeters = updatedSettings.VisitedMinRadiusMeters;
                    currentSettings.VisitedMaxRadiusMeters = updatedSettings.VisitedMaxRadiusMeters;
                    currentSettings.VisitedAccuracyMultiplier = updatedSettings.VisitedAccuracyMultiplier;
                    currentSettings.VisitedAccuracyRejectMeters = updatedSettings.VisitedAccuracyRejectMeters;
                    currentSettings.VisitedMaxSearchRadiusMeters = updatedSettings.VisitedMaxSearchRadiusMeters;
                    currentSettings.VisitedPlaceNotesSnapshotMaxHtmlChars = updatedSettings.VisitedPlaceNotesSnapshotMaxHtmlChars;

                    await _dbContext.SaveChangesAsync();

                    if (shouldPurgeTileCache)
                    {
                        QueueTileCachePurge();
                    }

                    // Audit settings update with changed fields summary
                    if (changes.Count > 0)
                    {
                        LogAudit("SettingsUpdate", "Application settings updated", string.Join(", ", changes));
                    }
                }

                _settingsService.RefreshSettings();

                SetAlert("Settings updated and refreshed successfully.", "success");
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                SetTileProviderViewData();
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
                    success = true,
                    message = "The map tile cache has been deleted successfully.",
                    cacheStatus
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
                    message = "The map tile cache for zoom levels equal or greater of 9, has been deleted successfully.",
                    cacheStatus
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
        /// Normalizes and validates tile provider settings, applying presets when selected.
        /// </summary>
        private void NormalizeTileProviderSettings(ApplicationSettings currentSettings, ApplicationSettings updatedSettings)
        {
            var providerKey = updatedSettings.TileProviderKey?.Trim();
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                // Preserve existing settings when tile provider fields are not posted.
                updatedSettings.TileProviderKey = currentSettings.TileProviderKey;
                updatedSettings.TileProviderUrlTemplate = currentSettings.TileProviderUrlTemplate;
                updatedSettings.TileProviderAttribution = currentSettings.TileProviderAttribution;
                updatedSettings.TileProviderApiKey = currentSettings.TileProviderApiKey;
                return;
            }
            var preset = TileProviderCatalog.FindPreset(providerKey);
            var isCustom = string.Equals(providerKey, TileProviderCatalog.CustomProviderKey, StringComparison.OrdinalIgnoreCase);

            if (preset == null && !isCustom)
            {
                ModelState.AddModelError(nameof(ApplicationSettings.TileProviderKey), "Unknown tile provider selection.");
                return;
            }

            if (preset != null)
            {
                updatedSettings.TileProviderKey = preset.Key;
                updatedSettings.TileProviderUrlTemplate = preset.UrlTemplate;
                updatedSettings.TileProviderAttribution = preset.Attribution;
            }
            else
            {
                updatedSettings.TileProviderKey = TileProviderCatalog.CustomProviderKey;
            }

            if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderAttribution))
            {
                ModelState.AddModelError(nameof(ApplicationSettings.TileProviderAttribution), "Attribution is required.");
            }

            if (!TileProviderCatalog.TryValidateTemplate(updatedSettings.TileProviderUrlTemplate, out var templateError))
            {
                ModelState.AddModelError(nameof(ApplicationSettings.TileProviderUrlTemplate), templateError);
            }

            if (TileProviderCatalog.RequiresApiKey(updatedSettings.TileProviderUrlTemplate))
            {
                if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderApiKey))
                {
                    updatedSettings.TileProviderApiKey = currentSettings.TileProviderApiKey;
                }

                if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderApiKey))
                {
                    ModelState.AddModelError(nameof(ApplicationSettings.TileProviderApiKey),
                        "API key is required for the selected tile provider.");
                }
            }
            else
            {
                updatedSettings.TileProviderApiKey = null;
            }
        }

        /// <summary>
        /// Adds tile provider preset metadata needed by the settings view.
        /// </summary>
        private void SetTileProviderViewData()
        {
            ViewData["TileProviderPresets"] = TileProviderCatalog.Presets;
            ViewData["TileProviderCustomKey"] = TileProviderCatalog.CustomProviderKey;
        }

        /// <summary>
        /// Purges the tile cache in the background to avoid blocking the settings update.
        /// </summary>
        private void QueueTileCachePurge()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();
                    await tileCacheService.PurgeAllCacheAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to purge tile cache in background.");
                }
            });
        }

        /// <summary>
        /// Clears the mbtiles cache
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ClearMbtilesCache()
        {
            TempData["Message"] = "All MBTiles for mobile map cache cleared.";
            return RedirectToAction("Index");
        }
    }
}
