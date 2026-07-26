using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Public.Controllers;

// JS usage example 
// var baseUrl = window.location.origin;  // This will be "http://localhost:5000" in dev or "https://yourdomain.com" in prod
// var tileUrl = baseUrl + "/tiles/{z}/{x}/{y}.png";
// L.tileLayer(tileUrl, {
//     maxZoom: 19,
//     attribution: '&copy; OpenStreetMap contributors'
// }).addTo(map);
/// <summary>
/// Controller for serving cached map tiles via a proxy to upstream tile providers.
/// </summary>
[Area("Public")]
[Route("Public/tiles")]
public class TilesController : Controller
{
    /// <summary>
    /// Maximum supported zoom level for tile requests.
    /// Most tile providers support up to zoom 22, some go to 24.
    /// </summary>
    private const int MaxZoomLevel = 22;

    /// <summary>
    /// Retry-After header value (in seconds) sent with HTTP 503 when the outbound budget is exhausted.
    /// Set to 6s to align with <see cref="TileCacheService.OutboundBudget"/>: at 2 tokens/sec
    /// (ReplenishIntervalMs=500) with BurstCapacity=12, a full burst refills in ~6 seconds.
    /// Also exposed to the client via <c>wayfarerTileConfig.retryAfterSeconds</c> so the
    /// tile layer can derive its slow-retry interval without hardcoding the value.
    /// </summary>
    internal const int BudgetRetryAfterSeconds = 6;

    /// <summary>
    /// Thread-safe dictionary for rate limiting anonymous tile requests by IP address.
    /// Uses atomic operations via <see cref="RateLimitHelper"/> to prevent race conditions.
    /// Exposed internally for periodic background cleanup by <see cref="Wayfarer.Jobs.RateLimitCleanupJob"/>.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry> RateLimitCache = new();

    /// <summary>
    /// Thread-safe dictionary for rate limiting authenticated tile requests by user ID.
    /// Separate from anonymous rate limiting to apply different (higher) limits for trusted users
    /// while still preventing abuse from compromised accounts.
    /// Exposed internally for periodic background cleanup by <see cref="Wayfarer.Jobs.RateLimitCleanupJob"/>.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry> AuthRateLimitCache = new();

    /// <summary>
    /// Thread-safe dictionary for tracking per-IP outbound budget consumption (cache miss rate).
    /// Prevents a single IP from monopolizing the global outbound token budget by limiting how
    /// many upstream tile fetches a single client can trigger per minute.
    /// Uses the same sliding-window pattern as request rate limiting.
    /// Exposed internally for periodic background cleanup by <see cref="Wayfarer.Jobs.RateLimitCleanupJob"/>.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, RateLimitHelper.RateLimitEntry> OutboundBudgetCache = new();

    private readonly ILogger<TilesController> _logger;
    private readonly TileCacheService _tileCacheService;
    private readonly IApplicationSettingsService _settingsService;

    public TilesController(ILogger<TilesController> logger, TileCacheService tileCacheService, IApplicationSettingsService settingsService)
    {
        _logger = logger;
        _tileCacheService = tileCacheService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Endpoint to serve cached tiles.
    /// Example URL: /tiles/10/512/384.png
    /// </summary>
    [HttpGet("{z:int}/{x:int}/{y:int}.png")]
    public async Task<IActionResult> GetTile(int z, int x, int y)
    {
        // Validate the referer header to prevent third-party exploitation.
        // Intentionally restrictive: rejects requests without a same-origin Referer.
        // Acceptable because: mobile app fetches tiles directly from OSM (not this proxy),
        // embedded maps (iframes) work because tile requests originate same-origin,
        // and non-browser API clients are not expected to use this endpoint.
        // The rate limiter is the primary abuse defense; this is an additional deterrent.
        string? refererValue = Request.Headers["Referer"].ToString();
        if (string.IsNullOrEmpty(refererValue) || !IsValidReferer(refererValue))
        {
            _logger.LogWarning("Unauthorized tile request rejected by same-origin Referer policy.");
            return Unauthorized("Unauthorized request.");
        }

        // Validate tile coordinates are within acceptable bounds.
        // First check zoom level to safely calculate max tile index.
        if (z < 0 || z > MaxZoomLevel)
        {
            _logger.LogWarning("Invalid tile coordinates requested: z={Z}, x={X}, y={Y}", z, x, y);
            return BadRequest("Invalid tile coordinates.");
        }

        // At zoom level z, valid tile coordinates are 0 to 2^z - 1.
        var maxTileIndex = (1 << z) - 1; // 2^z - 1
        if (x < 0 || y < 0 || x > maxTileIndex || y > maxTileIndex)
        {
            _logger.LogWarning("Invalid tile coordinates requested: z={Z}, x={X}, y={Y}", z, x, y);
            return BadRequest("Invalid tile coordinates.");
        }

        // Resolve the tile provider template from settings or presets.
        var settings = _settingsService.GetSettings();

        // Rate limit strategy: anonymous by IP, authenticated by userId (mutually exclusive).
        // An authenticated user is NOT also counted against the IP limit. This avoids
        // unfairly penalizing users behind shared NATs (e.g., corporate networks).
        // The outbound budget (OutboundBudget) provides system-wide protection regardless.
        if (settings.TileRateLimitEnabled)
        {
            var userId = User.Identity?.IsAuthenticated == true
                ? User.FindFirstValue(ClaimTypes.NameIdentifier)
                : null;

            if (userId != null)
            {
                // Authenticated user with a valid user ID — rate limit by userId.
                if (RateLimitHelper.IsRateLimitExceeded(AuthRateLimitCache, userId, settings.TileRateLimitAuthenticatedPerMinute))
                {
                    TileCacheDiagnostics.ClientBudgetRejected(_logger, "request-authenticated");
                    _logger.LogWarning("Tile request rate limit exceeded for an authenticated client.");
                    Response.Headers["Retry-After"] = BudgetRetryAfterSeconds.ToString();
                    return StatusCode(429, "Too many requests. Please try again later.");
                }
            }
            else
            {
                // Authenticated user without a NameIdentifier claim — unexpected, log for diagnostics.
                // Falls back to the stricter anonymous (IP-based) rate limit as a safe-side default.
                if (User.Identity?.IsAuthenticated == true)
                {
                    _logger.LogWarning("Authenticated user without NameIdentifier claim — falling back to IP-based rate limiting");
                }

                // Anonymous user or authenticated user without a NameIdentifier claim — rate limit by IP.
                var clientIp = GetClientIpAddress();
                if (RateLimitHelper.IsRateLimitExceeded(RateLimitCache, clientIp, settings.TileRateLimitPerMinute))
                {
                    TileCacheDiagnostics.ClientBudgetRejected(_logger, "request-anonymous");
                    _logger.LogWarning("Tile request rate limit exceeded for an anonymous client.");
                    Response.Headers["Retry-After"] = BudgetRetryAfterSeconds.ToString();
                    return StatusCode(429, "Too many requests. Please try again later.");
                }
            }
        }
        var preset = TileProviderCatalog.FindPreset(settings.TileProviderKey);
        var template = preset?.UrlTemplate ?? settings.TileProviderUrlTemplate;
        var policy = TileProviderPolicyResolver.Resolve(settings, _logger);
        if (!policy.CanContactProvider)
        {
            _logger.LogWarning("Tile provider compatibility blocks the active configuration.");
            return StatusCode(503, "Tile provider configuration requires administrator attention.");
        }
        var providerIdentity = TileProviderCatalog.CreateCacheIdentity(
            settings.TileProviderKey, template);
        var apiKey = TileProviderCatalog.RequiresApiKey(template) ? settings.TileProviderApiKey : null;

        if (!TileProviderCatalog.TryBuildTileUrl(template, apiKey, z, x, y, out var tileUrl, out var error))
        {
            _logger.LogError("Tile provider configuration error: {Error}", error);
            return StatusCode(500, "Tile provider misconfigured.");
        }

        // Call the tile cache service to retrieve the tile.
        // The service will either return the cached tile data, signal budget exhaustion (503),
        // or indicate the tile was not found (404).
        var result = await _tileCacheService.RetrieveTileAsync(
            z.ToString(), x.ToString(), y.ToString(), tileUrl, HttpContext.RequestAborted,
            providerIdentity.Fingerprint, providerIdentity.CanAdoptLegacyOsm);

        if (result.Status is TileRetrievalStatus.BudgetRejected or TileRetrievalStatus.TransientFailure)
        {
            var retryAfter = result.RetryAfterSeconds ?? BudgetRetryAfterSeconds;
            _logger.LogWarning("Tile request ended with transient status {TileStatus}.", result.Status);
            Response.Headers["Retry-After"] = retryAfter.ToString();
            return StatusCode(503, "Tile server busy. Please retry shortly.");
        }

        if (result.Status == TileRetrievalStatus.NotFound)
        {
            _logger.LogDebug("Tile provider confirmed tile absence.");
            return NotFound("Tile not found.");
        }

        if (result.Status == TileRetrievalStatus.PermanentFailure)
        {
            _logger.LogWarning("Tile provider returned a permanent non-absence response.");
            return StatusCode(StatusCodes.Status502BadGateway, "Tile provider rejected the request.");
        }

        if (result.TileData == null)
        {
            _logger.LogError("Successful tile retrieval returned no tile data.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Tile retrieval failed.");
        }

        // Set browser cache headers. Tiles are stable and rarely change;
        // 1-day browser caching eliminates redundant requests.
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        // Prevent browsers from MIME-sniffing PNG responses as a different content type.
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // Return the tile data with the appropriate content type.
        return File(result.TileData, "image/png");
    }

    /// <summary>
    /// Best-effort check that the request's Referer header matches our own domain.
    /// This is an abuse deterrent for accidental third-party embedding, not a security
    /// boundary — the Referer header is trivially spoofable by non-browser clients.
    /// The rate limiter is the primary defense against tile proxy abuse.
    /// </summary>
    private bool IsValidReferer(string referer)
    {
        if (string.IsNullOrEmpty(referer))
            return false;

        try
        {
            var refererUri = new Uri(referer);
            var requestHost = Request.Host.Host;
            return refererUri.Host == requestHost;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the client IP address using the shared <see cref="RateLimitHelper.GetClientIpAddress"/> utility.
    /// </summary>
    private string GetClientIpAddress() => RateLimitHelper.GetClientIpAddress(HttpContext);
}
