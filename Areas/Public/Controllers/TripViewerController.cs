using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;

namespace Wayfarer.Areas.Public.Controllers;

public class TripViewerController : BaseController
{
    private readonly HttpClient _httpClient;
    private readonly ITripThumbnailService _thumbnailService;

    public TripViewerController(
        ILogger<TripViewerController> logger,
        ApplicationDbContext dbContext,
        HttpClient httpClient,
        ITripThumbnailService thumbnailService)
        : base(logger, dbContext)
    {
        _httpClient = httpClient;
        _thumbnailService = thumbnailService;
    }

    /// <summary>
    /// Displays the public trips index with search, filtering, and pagination.
    /// </summary>
    /// <param name="q">Search query (searches Name and Notes)</param>
    /// <param name="view">View mode: "grid" or "list" (default: "grid")</param>
    /// <param name="sort">Sort option: "updated_desc", "name_asc", "name_desc" (default: "updated_desc")</param>
    /// <param name="page">Page number (1-based, default: 1)</param>
    /// <param name="pageSize">Items per page (default: 24, max: 60)</param>
    /// <returns>Index view with public trips</returns>
    [HttpGet]
    [Route("/Public/Trips", Name = "PublicTripsIndex", Order = 0)]
    [AllowAnonymous]
    public async Task<IActionResult> Index(string? q, string? view, string? sort, int page = 1, int pageSize = 24)
    {
        // Validate and normalize parameters
        view = view?.ToLowerInvariant() == "list" ? "list" : "grid";
        sort = sort?.ToLowerInvariant() switch
        {
            "name_asc" => "name_asc",
            "name_desc" => "name_desc",
            _ => "updated_desc"
        };
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 60);

        // Start with public trips only
        var query = _dbContext.Trips
            .Where(t => t.IsPublic)
            .AsQueryable();

        // Apply search filter (case-insensitive)
        if (!string.IsNullOrWhiteSpace(q))
        {
            var searchTerm = q.Trim();
            query = query.Where(t =>
                EF.Functions.ILike(t.Name, $"%{searchTerm}%") ||
                (t.Notes != null && EF.Functions.ILike(t.Notes, $"%{searchTerm}%")));
        }

        // Get total count for pagination
        var total = await query.CountAsync();

        // Apply sorting
        query = sort switch
        {
            "name_asc" => query.OrderBy(t => t.Name),
            "name_desc" => query.OrderByDescending(t => t.Name),
            _ => query.OrderByDescending(t => t.UpdatedAt)
        };

        // Project to PublicTripIndexItem with counts
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new PublicTripIndexItem
            {
                Id = t.Id,
                Name = t.Name,
                NotesExcerpt = t.Notes != null ? t.Notes.Substring(0, Math.Min(140, t.Notes.Length)) : null,
                CoverImageUrl = t.CoverImageUrl,
                CenterLat = t.CenterLat,
                CenterLon = t.CenterLon,
                Zoom = t.Zoom,
                UpdatedAt = t.UpdatedAt,
                RegionsCount = t.Regions!.Count(),
                PlacesCount = t.Regions!.Where(r => r.Places != null).SelectMany(r => r.Places!).Count(),
                SegmentsCount = t.Segments!.Count()
            })
            .ToListAsync();

        // Strip HTML from NotesExcerpt and generate thumbnails
        var thumbnailSize = view == "list" ? "320x180" : "800x450";

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.NotesExcerpt))
            {
                // Strip HTML tags to plain text
                item.NotesExcerpt = Regex.Replace(item.NotesExcerpt, "<.*?>", string.Empty);
                // Trim to ~140 characters and add ellipsis if needed
                if (item.NotesExcerpt.Length > 140)
                {
                    item.NotesExcerpt = item.NotesExcerpt.Substring(0, 137) + "...";
                }
            }

            // Don't generate thumbnails during page load - they'll be loaded asynchronously via API
            // This prevents blocking the page response
            item.ThumbUrl = null;
        }

        // Build view model
        var viewModel = new PublicTripIndexVm
        {
            Items = items,
            Q = q,
            View = view,
            Sort = sort,
            Page = page,
            PageSize = pageSize,
            Total = total
        };

        // Set page metadata
        ViewData["Title"] = "Public Trips";
        ViewData["LoadLeaflet"] = false; // No map on index page
        ViewData["LoadQuill"] = false;

        return View("~/Areas/Public/Views/TripViewer/Index.cshtml", viewModel);
    }

    // GET: /Public/Trips/View/{id}?embed=true
    [HttpGet]
    [Route("/Public/Trips/{id}", Order = 2)]
    public async Task<IActionResult> View(Guid id, bool embed = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var trip = await _dbContext.Trips
            .Include(t => t.Regions!).ThenInclude(r => r.Places!)
            .Include(t => t.Regions!).ThenInclude(a => a.Areas)
            .Include(t => t.Segments!)
            .FirstOrDefaultAsync(t => t.Id == id);

        // Check if trip exists and is public before accessing properties
        if (trip == null || !trip.IsPublic)
        {
            return NotFound();
        }

        var owner = trip.UserId == userId;
        
        /* ---- layout flags ---- */
        ViewData["LoadLeaflet"] = true;      // needs map
        ViewData["LoadQuill"]   = false;     // no editor
        ViewData["BodyClass"]   = "container-fluid";  // full-width

        ViewBag.IsOwner = owner;
        ViewBag.IsEmbed = embed;             // not an iframe here

        return View("~/Views/Trip/Viewer.cshtml", trip);
    }

    /// <summary>
    /// Returns a quick preview partial for a trip (used in modal on index page).
    /// </summary>
    /// <param name="id">Trip identifier</param>
    /// <returns>Partial view with trip preview</returns>
    [HttpGet]
    [Route("/Public/Trips/Preview/{id}", Order = 1)]
    [AllowAnonymous]
    public async Task<IActionResult> Preview(Guid id)
    {
        var trip = await _dbContext.Trips
            .Where(t => t.IsPublic && t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Notes,
                t.CoverImageUrl,
                t.CenterLat,
                t.CenterLon,
                t.Zoom,
                t.UpdatedAt,
                RegionsCount = t.Regions!.Count(),
                PlacesCount = t.Regions!.Where(r => r.Places != null).SelectMany(r => r.Places!).Count(),
                SegmentsCount = t.Segments!.Count()
            })
            .FirstOrDefaultAsync();

        if (trip == null)
        {
            return NotFound();
        }

        // Create view model for preview
        var previewItem = new PublicTripIndexItem
        {
            Id = trip.Id,
            Name = trip.Name,
            NotesExcerpt = trip.Notes,
            CoverImageUrl = trip.CoverImageUrl,
            CenterLat = trip.CenterLat,
            CenterLon = trip.CenterLon,
            Zoom = trip.Zoom,
            UpdatedAt = trip.UpdatedAt,
            RegionsCount = trip.RegionsCount,
            PlacesCount = trip.PlacesCount,
            SegmentsCount = trip.SegmentsCount
        };

        // Generate thumbnail for preview (larger size)
        previewItem.ThumbUrl = await _thumbnailService.GetThumbUrlAsync(
            previewItem.Id,
            previewItem.CenterLat,
            previewItem.CenterLon,
            previewItem.Zoom,
            previewItem.CoverImageUrl,
            previewItem.UpdatedAt,
            "800x450");

        return PartialView("~/Areas/Public/Views/TripViewer/_TripQuickView.cshtml", previewItem);
    }

    [AllowAnonymous]
    [HttpGet("Public/ProxyImage")]
    public async Task<IActionResult> ProxyImage(string url)
    {
        using var resp = await _httpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return StatusCode((int)resp.StatusCode);

        var contentType = resp.Content.Headers.ContentType?.MediaType
                          ?? "application/octet-stream";
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return File(bytes, contentType);
    }

    /// <summary>
    /// API endpoint to generate thumbnail for a specific trip asynchronously.
    /// Returns JSON with thumbnail URL.
    /// GET: /Public/Trips/{id}/Thumbnail?size=800x450
    /// </summary>
    [HttpGet]
    [Route("/Public/Trips/{id}/Thumbnail", Order = 0)]
    [AllowAnonymous]
    public async Task<IActionResult> GetThumbnail(Guid id, string size = "800x450")
    {
        // Get trip info needed for thumbnail generation
        var trip = await _dbContext.Trips
            .Where(t => t.IsPublic && t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.CoverImageUrl,
                t.CenterLat,
                t.CenterLon,
                t.Zoom,
                t.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (trip == null)
        {
            return NotFound(new { error = "Trip not found" });
        }

        try
        {
            // Generate thumbnail asynchronously
            var thumbUrl = await _thumbnailService.GetThumbUrlAsync(
                trip.Id,
                trip.CenterLat,
                trip.CenterLon,
                trip.Zoom,
                trip.CoverImageUrl,
                trip.UpdatedAt,
                size);

            return Json(new { tripId = id, thumbUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for trip {TripId}", id);
            // Fallback to cover image if thumbnail generation fails
            return Json(new { tripId = id, thumbUrl = trip.CoverImageUrl });
        }
    }
}