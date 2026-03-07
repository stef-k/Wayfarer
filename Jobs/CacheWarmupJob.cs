using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Jobs;

/// <summary>
/// Quartz job that pre-caches external images referenced in a trip's notes and cover images.
/// Extracts image URLs from Trip, Region, Place, and Area notes HTML and CoverImageUrl fields,
/// then fetches and caches each via <see cref="IImageProxyService"/>.
/// Triggered on a debounced schedule after trip/region/place/area edits.
/// </summary>
[DisallowConcurrentExecution]
public class CacheWarmupJob : IJob
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IImageProxyService _imageProxyService;
    private readonly ILogger<CacheWarmupJob> _logger;

    public CacheWarmupJob(
        ApplicationDbContext dbContext,
        IImageProxyService imageProxyService,
        ILogger<CacheWarmupJob> logger)
    {
        _dbContext = dbContext;
        _imageProxyService = imageProxyService;
        _logger = logger;
    }

    /// <summary>
    /// Executes the warm-up: loads the trip and all child entities, extracts external
    /// image URLs, and caches each one via <see cref="IImageProxyService.FetchAndCacheAsync"/>.
    /// Individual image failures are logged but do not abort the job.
    /// </summary>
    public async Task Execute(IJobExecutionContext context)
    {
        var tripIdStr = context.JobDetail.JobDataMap.GetString("tripId");
        if (!Guid.TryParse(tripIdStr, out var tripId))
        {
            _logger.LogWarning("CacheWarmupJob received invalid tripId: {TripId}.", tripIdStr);
            return;
        }

        var ct = context.CancellationToken;

        _logger.LogInformation("Starting cache warm-up for trip {TripId}.", tripId);

        var trip = await _dbContext.Trips
            .Include(t => t.Regions!).ThenInclude(r => r.Places!)
            .Include(t => t.Regions!).ThenInclude(r => r.Areas!)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tripId, ct);

        if (trip == null)
        {
            _logger.LogWarning("CacheWarmupJob: trip {TripId} not found.", tripId);
            return;
        }

        // Collect all external image URLs
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Trip-level
        ExtractImageUrls(trip.Notes, urls);
        AddIfExternal(trip.CoverImageUrl, urls);

        // Regions
        foreach (var region in trip.Regions ?? Enumerable.Empty<Region>())
        {
            ExtractImageUrls(region.Notes, urls);
            AddIfExternal(region.CoverImageUrl, urls);

            // Places
            foreach (var place in region.Places ?? Enumerable.Empty<Place>())
            {
                ExtractImageUrls(place.Notes, urls);
            }

            // Areas
            foreach (var area in region.Areas ?? Enumerable.Empty<Area>())
            {
                ExtractImageUrls(area.Notes, urls);
            }
        }

        if (urls.Count == 0)
        {
            _logger.LogDebug("CacheWarmupJob: no external images found for trip {TripId}.", tripId);
            return;
        }

        _logger.LogInformation("CacheWarmupJob: warming {Count} image URLs for trip {TripId}.", urls.Count, tripId);

        var cached = 0;
        var failed = 0;

        foreach (var url in urls)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var result = await _imageProxyService.FetchAndCacheAsync(url, ct);
                if (result) cached++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "CacheWarmupJob: failed to cache image {Url} for trip {TripId}.", url, tripId);
            }
        }

        _logger.LogInformation(
            "CacheWarmupJob completed for trip {TripId}: {Cached} newly cached, {Failed} failed, {Skipped} already cached.",
            tripId, cached, failed, urls.Count - cached - failed);
    }

    /// <summary>
    /// Extracts external http(s) image URLs from &lt;img src="..."&gt; tags in HTML content.
    /// Delegates to <see cref="HtmlHelpers.ExtractExternalImageUrls"/> for regex consistency.
    /// </summary>
    private static void ExtractImageUrls(string? html, HashSet<string> urls)
    {
        foreach (var url in HtmlHelpers.ExtractExternalImageUrls(html))
        {
            urls.Add(url);
        }
    }

    /// <summary>
    /// Adds a URL to the set if it is an absolute external http(s) URL.
    /// Ignores relative paths and data URIs.
    /// </summary>
    private static void AddIfExternal(string? url, HashSet<string> urls)
    {
        if (!string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            urls.Add(url);
        }
    }
}
