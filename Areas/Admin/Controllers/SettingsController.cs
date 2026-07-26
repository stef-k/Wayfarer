using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Authorize(Roles = "Admin")]
    [Area("Admin")]
    public partial class SettingsController : BaseController
    {
        /// <summary>
        /// SSE channel name for broadcasting tile cache purge progress to admin clients.
        /// </summary>
        public const string TileCachePurgeChannel = "admin-tile-cache-purge";

        private readonly IApplicationSettingsService _settingsService;
        private readonly TileCacheService _tileCacheService;
        private readonly IProxiedImageCacheService _imageCacheService;
        private readonly IWebHostEnvironment _env;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SseService _sseService;

        public SettingsController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            IApplicationSettingsService settingsService,
            TileCacheService tileCacheService,
            IProxiedImageCacheService imageCacheService,
            IWebHostEnvironment env,
            IServiceScopeFactory scopeFactory,
            SseService sseService)
            : base(logger, dbContext)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _tileCacheService = tileCacheService ?? throw new ArgumentNullException(nameof(tileCacheService));
            _imageCacheService = imageCacheService ?? throw new ArgumentNullException(nameof(imageCacheService));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _sseService = sseService ?? throw new ArgumentNullException(nameof(sseService));
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

            if (settings.TileMetadataHotCacheSizeMB == 0)
            {
                settings.TileMetadataHotCacheSizeMB = ApplicationSettings.DefaultTileMetadataHotCacheSizeMB;
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

            // Image proxy cache stats
            ViewData["ImageCacheCount"] = await _imageCacheService.GetCachedImageCountAsync();
            double imageCacheSizeMB = await _imageCacheService.GetCacheSizeInMbAsync();
            ViewData["ImageCacheSizeMB"] = Math.Round(imageCacheSizeMB, 2);

            // Combined (tile cache + image cache + uploads)
            double combinedTotalMB = tileCacheSizeMB + imageCacheSizeMB + uploadsSizeMB;
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

            // OSM tile usage policy requires tiles to be cached for at least 7 days.
            // 256 MB is the minimum floor that can reasonably hold 7 days of tiles.
            // -1 (disable cache) and 0 (use default) are allowed; values 1-255 are rejected.
            if (updatedSettings.MaxCacheTileSizeInMB is > 0 and < 256)
            {
                ModelState.AddModelError(nameof(updatedSettings.MaxCacheTileSizeInMB),
                    "Minimum cache size is 256 MB (OSM requires at least 7 days of cached tiles). Use -1 to disable.");
            }

            // Authenticated users should always have at least the same rate limit as anonymous users.
            if (updatedSettings.TileRateLimitAuthenticatedPerMinute < updatedSettings.TileRateLimitPerMinute)
            {
                ModelState.AddModelError(nameof(updatedSettings.TileRateLimitAuthenticatedPerMinute),
                    "Authenticated rate limit must be equal to or greater than the anonymous rate limit.");
            }

            if (updatedSettings.TileMetadataHotCacheSizeMB != -1 &&
                (updatedSettings.TileMetadataHotCacheSizeMB < 16 || updatedSettings.TileMetadataHotCacheSizeMB > 512))
            {
                ModelState.AddModelError(nameof(updatedSettings.TileMetadataHotCacheSizeMB),
                    "Tile metadata hot cache size must be -1 (disable) or between 16 and 512 MB.");
            }

            if (currentSettings != null)
            {
                // Validate tile provider settings before model validation.
                NormalizeTileProviderSettings(currentSettings, updatedSettings);
                ValidateTileProviderPolicy(updatedSettings);
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
                    Track("MaxCacheImageSizeInMB", currentSettings.MaxCacheImageSizeInMB, updatedSettings.MaxCacheImageSizeInMB);
                    Track("ImageCacheExpiryDays", currentSettings.ImageCacheExpiryDays, updatedSettings.ImageCacheExpiryDays);
                    Track("MaxProxyImageDownloadMB", currentSettings.MaxProxyImageDownloadMB, updatedSettings.MaxProxyImageDownloadMB);
                    Track("UploadSizeLimitMB", currentSettings.UploadSizeLimitMB, updatedSettings.UploadSizeLimitMB);
                    var oldTilePolicy = TileProviderPolicyResolver.Resolve(currentSettings);
                    var newTilePolicy = TileProviderPolicyResolver.Resolve(updatedSettings);
                    Track("TileTrafficMode", oldTilePolicy.TrafficMode, newTilePolicy.TrafficMode);
                    Track("TileProviderCompatibility", oldTilePolicy.Compatibility.Status, newTilePolicy.Compatibility.Status);
                    Track("TileProviderCompatibilitySource", oldTilePolicy.Compatibility.Source, newTilePolicy.Compatibility.Source);
                    Track("TileEffectiveRate", oldTilePolicy.UsesRateTokens ? oldTilePolicy.SustainedRequestsPerSecond : 0, newTilePolicy.UsesRateTokens ? newTilePolicy.SustainedRequestsPerSecond : 0);
                    Track("TileEffectiveBurst", oldTilePolicy.UsesRateTokens ? oldTilePolicy.BurstCapacity : 0, newTilePolicy.UsesRateTokens ? newTilePolicy.BurstCapacity : 0);
                    Track("TileEffectiveConcurrency", oldTilePolicy.MaxConcurrency, newTilePolicy.MaxConcurrency);
                    Track("TileEffectiveClientSeriesPerMinute", oldTilePolicy.ClientSeriesPerMinute, newTilePolicy.ClientSeriesPerMinute);
                    Track("TileRateLimitEnabled", currentSettings.TileRateLimitEnabled, updatedSettings.TileRateLimitEnabled);
                    Track("TileRateLimitPerMinute", currentSettings.TileRateLimitPerMinute, updatedSettings.TileRateLimitPerMinute);
                    Track("TileRateLimitAuthenticatedPerMinute", currentSettings.TileRateLimitAuthenticatedPerMinute, updatedSettings.TileRateLimitAuthenticatedPerMinute);
                    Track("TileOutboundBudgetPerIpPerMinute", currentSettings.TileOutboundBudgetPerIpPerMinute, updatedSettings.TileOutboundBudgetPerIpPerMinute);
                    Track("TileProviderAdvancedLimitsEnabled", currentSettings.TileProviderAdvancedLimitsEnabled, updatedSettings.TileProviderAdvancedLimitsEnabled);
                    Track("TileMetadataHotCacheSizeMB", currentSettings.TileMetadataHotCacheSizeMB, updatedSettings.TileMetadataHotCacheSizeMB);
                    Track("ProxyImageRateLimitEnabled", currentSettings.ProxyImageRateLimitEnabled, updatedSettings.ProxyImageRateLimitEnabled);
                    Track("ProxyImageRateLimitPerMinute", currentSettings.ProxyImageRateLimitPerMinute, updatedSettings.ProxyImageRateLimitPerMinute);

                    // Trip Place Auto-Visited settings
                    Track("VisitedRequiredHits", currentSettings.VisitedRequiredHits, updatedSettings.VisitedRequiredHits);
                    Track("VisitedMinRadiusMeters", currentSettings.VisitedMinRadiusMeters, updatedSettings.VisitedMinRadiusMeters);
                    Track("VisitedMaxRadiusMeters", currentSettings.VisitedMaxRadiusMeters, updatedSettings.VisitedMaxRadiusMeters);
                    Track("VisitedAccuracyMultiplier", currentSettings.VisitedAccuracyMultiplier, updatedSettings.VisitedAccuracyMultiplier);
                    Track("VisitedAccuracyRejectMeters", currentSettings.VisitedAccuracyRejectMeters, updatedSettings.VisitedAccuracyRejectMeters);
                    Track("VisitedMaxSearchRadiusMeters", currentSettings.VisitedMaxSearchRadiusMeters, updatedSettings.VisitedMaxSearchRadiusMeters);
                    Track("VisitedPlaceNotesSnapshotMaxHtmlChars", currentSettings.VisitedPlaceNotesSnapshotMaxHtmlChars, updatedSettings.VisitedPlaceNotesSnapshotMaxHtmlChars);
                    Track("VisitNotificationCooldownHours", currentSettings.VisitNotificationCooldownHours, updatedSettings.VisitNotificationCooldownHours);
                    Track("VisitedSuggestionMaxRadiusMultiplier", currentSettings.VisitedSuggestionMaxRadiusMultiplier, updatedSettings.VisitedSuggestionMaxRadiusMultiplier);

                    currentSettings.IsRegistrationOpen = updatedSettings.IsRegistrationOpen;
                    currentSettings.LocationTimeThresholdMinutes = updatedSettings.LocationTimeThresholdMinutes;
                    currentSettings.LocationDistanceThresholdMeters = updatedSettings.LocationDistanceThresholdMeters;
                    currentSettings.LocationAccuracyThresholdMeters = updatedSettings.LocationAccuracyThresholdMeters;
                    currentSettings.MaxCacheTileSizeInMB = updatedSettings.MaxCacheTileSizeInMB;
                    currentSettings.MaxCacheImageSizeInMB = updatedSettings.MaxCacheImageSizeInMB;
                    currentSettings.ImageCacheExpiryDays = updatedSettings.ImageCacheExpiryDays;
                    currentSettings.MaxProxyImageDownloadMB = updatedSettings.MaxProxyImageDownloadMB;
                    currentSettings.UploadSizeLimitMB = updatedSettings.UploadSizeLimitMB;
                    currentSettings.TileProviderKey = updatedSettings.TileProviderKey;
                    currentSettings.TileProviderUrlTemplate = updatedSettings.TileProviderUrlTemplate;
                    currentSettings.TileProviderAttribution = updatedSettings.TileProviderAttribution;
                    currentSettings.TileProviderApiKey = updatedSettings.TileProviderApiKey;
                    currentSettings.TileTrafficMode = updatedSettings.TileTrafficMode;
                    currentSettings.TileRateLimitEnabled = updatedSettings.TileRateLimitEnabled;
                    currentSettings.TileRateLimitPerMinute = updatedSettings.TileRateLimitPerMinute;
                    currentSettings.TileRateLimitAuthenticatedPerMinute = updatedSettings.TileRateLimitAuthenticatedPerMinute;
                    currentSettings.TileOutboundBudgetPerIpPerMinute = updatedSettings.TileOutboundBudgetPerIpPerMinute;
                    currentSettings.TileProviderAdvancedLimitsEnabled = updatedSettings.TileProviderAdvancedLimitsEnabled;
                    currentSettings.TileProviderSustainedRequestsPerSecond = updatedSettings.TileProviderSustainedRequestsPerSecond;
                    currentSettings.TileProviderBurstCapacity = updatedSettings.TileProviderBurstCapacity;
                    currentSettings.TileProviderMaxConcurrency = updatedSettings.TileProviderMaxConcurrency;
                    currentSettings.TileProviderMaxAttempts = updatedSettings.TileProviderMaxAttempts;
                    currentSettings.TileProviderFallbackBaseDelayMs = updatedSettings.TileProviderFallbackBaseDelayMs;
                    currentSettings.TileProviderFallbackDelayCapSeconds = updatedSettings.TileProviderFallbackDelayCapSeconds;
                    currentSettings.TileProviderMaxIndividualWaitSeconds = updatedSettings.TileProviderMaxIndividualWaitSeconds;
                    currentSettings.TileProviderTotalRetryCeilingSeconds = updatedSettings.TileProviderTotalRetryCeilingSeconds;
                    currentSettings.TileOutboundBudgetHistorical30Acknowledged =
                        updatedSettings.TileOutboundBudgetPerIpPerMinute == 30 &&
                        currentSettings.TileOutboundBudgetHistorical30Acknowledged;
                    currentSettings.TileMetadataHotCacheSizeMB = updatedSettings.TileMetadataHotCacheSizeMB;
                    currentSettings.ProxyImageRateLimitEnabled = updatedSettings.ProxyImageRateLimitEnabled;
                    currentSettings.ProxyImageRateLimitPerMinute = updatedSettings.ProxyImageRateLimitPerMinute;

                    // Trip Place Auto-Visited settings
                    currentSettings.VisitedRequiredHits = updatedSettings.VisitedRequiredHits;
                    currentSettings.VisitedMinRadiusMeters = updatedSettings.VisitedMinRadiusMeters;
                    currentSettings.VisitedMaxRadiusMeters = updatedSettings.VisitedMaxRadiusMeters;
                    currentSettings.VisitedAccuracyMultiplier = updatedSettings.VisitedAccuracyMultiplier;
                    currentSettings.VisitedAccuracyRejectMeters = updatedSettings.VisitedAccuracyRejectMeters;
                    currentSettings.VisitedMaxSearchRadiusMeters = updatedSettings.VisitedMaxSearchRadiusMeters;
                    currentSettings.VisitedPlaceNotesSnapshotMaxHtmlChars = updatedSettings.VisitedPlaceNotesSnapshotMaxHtmlChars;
                    currentSettings.VisitNotificationCooldownHours = updatedSettings.VisitNotificationCooldownHours;
                    currentSettings.VisitedSuggestionMaxRadiusMultiplier = updatedSettings.VisitedSuggestionMaxRadiusMultiplier;

                    await _dbContext.SaveChangesAsync();

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

        /// <summary>
        /// Queues a full tile cache purge as a background operation.
        /// Returns 202 Accepted immediately; progress is reported via SSE on
        /// <see cref="TileCachePurgeChannel"/>.
        /// Returns 409 Conflict if a purge is already running.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteAllMapTileCache()
        {
            if (TileCacheService.IsPurgeInProgress)
                return Conflict(new { success = false, message = "A cache purge is already in progress." });

            QueuePurgeOperation("all");
            return Accepted(new { success = true, message = "Full cache purge started." });
        }

        /// <summary>
        /// Queues an LRU tile cache purge (zoom >= 9) as a background operation.
        /// Returns 202 Accepted immediately; progress is reported via SSE on
        /// <see cref="TileCachePurgeChannel"/>.
        /// Returns 409 Conflict if a purge is already running.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteLruCache()
        {
            if (TileCacheService.IsPurgeInProgress)
                return Conflict(new { success = false, message = "A cache purge is already in progress." });

            QueuePurgeOperation("lru");
            return Accepted(new { success = true, message = "LRU cache purge started." });
        }

        /// <summary>
        /// SSE endpoint for receiving real-time tile cache purge progress events.
        /// Admin clients connect here after initiating a purge or on page load
        /// when a purge is already in progress.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> TileCachePurgeSse(CancellationToken cancellationToken)
        {
            await _sseService.SubscribeAsync(
                TileCachePurgeChannel,
                Response,
                cancellationToken,
                enableHeartbeat: true,
                heartbeatInterval: TimeSpan.FromSeconds(30));
            return new EmptyResult();
        }

        /// <summary>
        /// Returns whether a tile cache purge is currently in progress.
        /// Used by the admin UI on page load to detect and reconnect to an ongoing purge.
        /// </summary>
        [HttpGet]
        public IActionResult TileCachePurgeStatus()
        {
            return Ok(new { inProgress = TileCacheService.IsPurgeInProgress });
        }

        /// <summary>
        /// Fires a cache purge in the background with SSE progress reporting.
        /// The purge methods broadcast "started" after acquiring the guard, and this
        /// method broadcasts "completed" or "failed" based on the outcome.
        /// Uses the captured <see cref="_sseService"/> singleton directly instead of
        /// re-resolving from a new DI scope.
        /// </summary>
        private void QueuePurgeOperation(string purgeType)
        {
            // Capture the singleton reference for the background task — avoids
            // re-resolving the same singleton from a new DI scope.
            var sseService = _sseService;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();

                    // "started" is broadcast inside the purge methods after the
                    // CompareExchange guard succeeds — no dangling "started" on TOCTOU race.
                    if (purgeType == "lru")
                        await tileCacheService.PurgeLRUCacheAsync(sseService, TileCachePurgeChannel);
                    else
                        await tileCacheService.PurgeAllCacheAsync(sseService, TileCachePurgeChannel);

                    // Broadcast final cache status so the UI can update counters.
                    var cacheStatus = await BuildCacheStatusAsync(tileCacheService);
                    await sseService.BroadcastAsync(TileCachePurgeChannel,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            eventType = "completed",
                            purgeType,
                            message = purgeType == "lru"
                                ? "LRU cache purge completed successfully."
                                : "Full cache purge completed successfully.",
                            cacheStatus
                        }));
                }
                catch (InvalidOperationException)
                {
                    // Another purge won the CompareExchange race between the controller's
                    // IsPurgeInProgress check and the service's atomic guard. Safe to ignore —
                    // the winning request is already broadcasting progress. No "started" event
                    // was sent for the losing request (it's broadcast after the guard).
                    _logger.LogInformation("Background {PurgeType} purge skipped: concurrent purge is running.", purgeType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background {PurgeType} cache purge failed.", purgeType);
                    try
                    {
                        await sseService.BroadcastAsync(TileCachePurgeChannel,
                            System.Text.Json.JsonSerializer.Serialize(new
                            {
                                eventType = "failed",
                                purgeType,
                                errorMessage = ex.Message
                            }));
                    }
                    catch (Exception broadcastEx)
                    {
                        _logger.LogDebug(broadcastEx, "Failed to broadcast purge failure event.");
                    }
                }
            });
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

        /// <summary>
        /// Builds cache status from a <see cref="TileCacheService"/> instance.
        /// Used by both the request-scoped path and the background purge task.
        /// </summary>
        private static async Task<CacheStatus> BuildCacheStatusAsync(TileCacheService tileCacheService)
        {
            var cacheStatus = new CacheStatus();
            double total = await tileCacheService.GetCacheFileSizeInMbAsync();
            double lru = await tileCacheService.GetLruCachedInMbFilesAsync();

            cacheStatus.TotalCacheFiles = await tileCacheService.GetTotalCachedFilesAsync();
            cacheStatus.LruTotalFiles = await tileCacheService.GetLruTotalFilesInDbAsync();
            cacheStatus.TotalCacheSize = Math.Round(total, 2);
            cacheStatus.TotalCacheSizeGB = Math.Round(total / 1024, 3);
            cacheStatus.TotalLru = Math.Round(lru, 2);
            cacheStatus.TotalLruGB = Math.Round(lru / 1024, 3);

            return cacheStatus;
        }

        /// <summary>
        /// Retrieves cache status using the request-scoped tile cache service.
        /// </summary>
        private Task<CacheStatus> GetCacheStatus() => BuildCacheStatusAsync(_tileCacheService);

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

            // Sanitize attribution HTML to prevent XSS attacks.
            updatedSettings.TileProviderAttribution = HtmlSanitization.SanitizeAttribution(updatedSettings.TileProviderAttribution);

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
