using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;

namespace Wayfarer.Areas.Public.Controllers;

public class TripViewerController : BaseController
{
    private readonly HttpClient _httpClient;
    private readonly ITripThumbnailService _thumbnailService;
    private readonly ITripTagService _tripTagService;

    public TripViewerController(
        ILogger<TripViewerController> logger,
        ApplicationDbContext dbContext,
        HttpClient httpClient,
        ITripThumbnailService thumbnailService,
        ITripTagService tripTagService)
        : base(logger, dbContext)
    {
        _httpClient = httpClient;
        _thumbnailService = thumbnailService;
        _tripTagService = tripTagService;
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
    public async Task<IActionResult> Index(
        string? q,
        string? view,
        string? sort,
        string? tags,
        string? tagMode,
        int page = 1,
        int pageSize = 24)
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

        // Get current user ID for IsOwner check
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Start with public trips only
        var query = _dbContext.Trips
            .Include(t => t.User)
            .Include(t => t.Tags)
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

        var parsedTagSlugs = ParseTagSlugs(tags);
        var normalizedTagMode = string.Equals(tagMode, "any", StringComparison.OrdinalIgnoreCase) ? "any" : "all";
        if (parsedTagSlugs.Length > 0)
        {
            query = _tripTagService.ApplyTagFilter(query, parsedTagSlugs, normalizedTagMode);
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
                OwnerDisplayName = t.User.DisplayName,
                Name = t.Name,
                NotesExcerpt = t.Notes != null ? t.Notes.Substring(0, Math.Min(140, t.Notes.Length)) : null,
                CoverImageUrl = t.CoverImageUrl,
                CenterLat = t.CenterLat,
                CenterLon = t.CenterLon,
                Zoom = t.Zoom,
                UpdatedAt = t.UpdatedAt,
                RegionsCount = t.Regions!.Count(),
                PlacesCount = t.Regions!.Where(r => r.Places != null).SelectMany(r => r.Places!).Count(),
                SegmentsCount = t.Segments!.Count(),
                IsOwner = t.UserId == currentUserId,
                Tags = t.Tags
                    .OrderBy(tag => tag.Name)
                    .Select(tag => new TripTagDto(tag.Id, tag.Name, tag.Slug))
                    .ToList()
            })
            .ToListAsync();

        // Strip HTML from NotesExcerpt and generate thumbnails
        var thumbnailSize = view == "list" ? "320x180" : "800x450";

        foreach (var item in items)
        {
            // Strip images from HTML notes for excerpt but keep other HTML formatting
            if (!string.IsNullOrWhiteSpace(item.NotesExcerpt))
            {
                // Remove <img> tags (including self-closing variants and with any attributes)
                item.NotesExcerpt = Regex.Replace(item.NotesExcerpt, @"<img[^>]*/?>", string.Empty, RegexOptions.IgnoreCase);
                // Remove background-image CSS properties
                item.NotesExcerpt = Regex.Replace(item.NotesExcerpt, @"background-image\s*:\s*url\([^)]*\)", string.Empty, RegexOptions.IgnoreCase);

                // Strip ALL HTML tags including incomplete/malformed ones
                // First strip complete tags
                item.NotesExcerpt = Regex.Replace(item.NotesExcerpt, "<[^>]*>", string.Empty);
                // Then strip any remaining < characters (from incomplete tags)
                item.NotesExcerpt = Regex.Replace(item.NotesExcerpt, "<.*", string.Empty);

                // Trim to 140 characters
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
        IReadOnlyList<TripTagDto> selectedTags = parsedTagSlugs.Length == 0
            ? Array.Empty<TripTagDto>()
            : await _dbContext.Tags
                .Where(t => parsedTagSlugs.Contains(t.Slug))
                .Select(t => new TripTagDto(t.Id, t.Name, t.Slug))
                .ToListAsync();

        var popularTags = await _tripTagService.GetPopularAsync(20);

        var viewModel = new PublicTripIndexVm
        {
            Items = items,
            Q = q,
            View = view,
            Sort = sort,
            Page = page,
            PageSize = pageSize,
            Total = total,
            TagsCsv = string.Join(',', parsedTagSlugs),
            TagMode = normalizedTagMode,
            SelectedTags = selectedTags,
            PopularTags = popularTags
        };

        // Set page metadata
        ViewData["Title"] = "Public Trips";
        ViewData["LoadLeaflet"] = false; // No map on index page
        ViewData["LoadQuill"] = false;

        return View("~/Areas/Public/Views/TripViewer/Index.cshtml", viewModel);
    }

    /// <summary>
    /// Redirects from tag-based URL to index with tag filter.
    /// GET: /Public/Trips/tag/{slug}
    /// </summary>
    [HttpGet]
    [Route("/Public/Trips/tag/{slug}", Name = "PublicTripsByTag", Order = 0)]
    [AllowAnonymous]
    public IActionResult ByTag(string slug, string? view, string? sort, int page = 1)
    {
        return RedirectToRoute("PublicTripsIndex", new { tags = slug, view, sort, page });
    }

    // GET: /Public/Trips/View/{id}?embed=true
    [HttpGet]
    [Route("/Public/Trips/{id}", Order = 2)]
    public async Task<IActionResult> View(Guid id, bool embed = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var trip = await _dbContext.Trips
            .Include(t => t.User)
            .Include(t => t.Tags)
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
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var trip = await _dbContext.Trips
            .Include(t => t.User)
            .Include(t => t.Tags)
            .Where(t => t.IsPublic && t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.UserId,
                OwnerDisplayName = t.User.DisplayName,
                t.Name,
                t.Notes,
                t.CoverImageUrl,
                t.CenterLat,
                t.CenterLon,
                t.Zoom,
                t.UpdatedAt,
                RegionsCount = t.Regions!.Count(),
                PlacesCount = t.Regions!.Where(r => r.Places != null).SelectMany(r => r.Places!).Count(),
                SegmentsCount = t.Segments!.Count(),
                Tags = t.Tags
                    .OrderBy(tag => tag.Name)
                    .Select(tag => new TripTagDto(tag.Id, tag.Name, tag.Slug))
                    .ToList()
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
            OwnerDisplayName = trip.OwnerDisplayName,
            Name = trip.Name,
            NotesExcerpt = trip.Notes,
            CoverImageUrl = trip.CoverImageUrl,
            CenterLat = trip.CenterLat,
            CenterLon = trip.CenterLon,
            Zoom = trip.Zoom,
            UpdatedAt = trip.UpdatedAt,
            RegionsCount = trip.RegionsCount,
            PlacesCount = trip.PlacesCount,
            SegmentsCount = trip.SegmentsCount,
            IsOwner = trip.UserId == currentUserId,
            Tags = trip.Tags
        };

        // Strip images from notes excerpt for preview modal but keep HTML formatting
        if (!string.IsNullOrWhiteSpace(previewItem.NotesExcerpt))
        {
            // Remove <img> tags (including self-closing variants and with any attributes)
            previewItem.NotesExcerpt = Regex.Replace(previewItem.NotesExcerpt, @"<img[^>]*/?>", string.Empty, RegexOptions.IgnoreCase);
            // Remove background-image CSS properties
            previewItem.NotesExcerpt = Regex.Replace(previewItem.NotesExcerpt, @"background-image\s*:\s*url\([^)]*\)", string.Empty, RegexOptions.IgnoreCase);

            // Strip HTML for character counting to get accurate length
            var plainText = Regex.Replace(previewItem.NotesExcerpt, "<.*?>", string.Empty);

            // Trim HTML to ~200 characters of plain text content
            if (plainText.Length > 200)
            {
                // Find approximately where to cut the HTML
                var targetLength = 197;
                var tempPlain = string.Empty;
                var htmlLength = 0;

                // Walk through HTML until we hit the character limit
                while (tempPlain.Length < targetLength && htmlLength < previewItem.NotesExcerpt.Length)
                {
                    tempPlain = Regex.Replace(previewItem.NotesExcerpt.Substring(0, htmlLength), "<.*?>", string.Empty);
                    htmlLength++;
                }

                previewItem.NotesExcerpt = previewItem.NotesExcerpt.Substring(0, Math.Min(htmlLength, previewItem.NotesExcerpt.Length)) + "...";
            }
        }

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

    /// <summary>
    /// Proxy external images with optional optimization.
    /// Default: 95% quality (visually lossless, faster loading)
    /// Print: Can specify maxWidth=600&quality=85 for smaller PDFs
    /// Disable: Use optimize=false to serve original image
    /// GET: /Public/ProxyImage?url=...&maxWidth=600&quality=85&optimize=true
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Public/ProxyImage")]
    public async Task<IActionResult> ProxyImage(string url, int? maxWidth = null, int? maxHeight = null, int? quality = null, bool optimize = true)
    {
        using var resp = await _httpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return StatusCode((int)resp.StatusCode);

        var contentType = resp.Content.Headers.ContentType?.MediaType
                          ?? "application/octet-stream";
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        // Optimize images if enabled (default is true for performance)
        if (optimize && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Default to 95% quality if not specified (visually lossless)
                // This provides faster loading with minimal quality loss
                var optimizedBytes = OptimizeImage(bytes, maxWidth, maxHeight, quality ?? 95, out bool isPng);
                bytes = optimizedBytes;
                // Content type depends on whether transparency was preserved
                contentType = isPng ? "image/png" : "image/jpeg";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to optimize image from {Url}, serving original", url);
                // Fall through to return original bytes
            }
        }

        return File(bytes, contentType);
    }

    /// <summary>
    /// Optimize image using ImageSharp - resize and compress while maintaining quality.
    /// Preserves PNG transparency for icons, converts photos to JPEG.
    /// Uses pure managed code with no native dependencies for cross-platform support.
    /// </summary>
    private byte[] OptimizeImage(byte[] imageBytes, int? maxWidth, int? maxHeight, int quality, out bool isPng)
    {
        using var inputStream = new MemoryStream(imageBytes);
        using var image = SixLabors.ImageSharp.Image.Load(inputStream);

        // Check if image has transparency (alpha channel)
        // PNG and WebP formats typically have alpha, JPEG does not
        bool hasTransparency = image.Metadata.DecodedImageFormat?.Name == "PNG" ||
                               image.Metadata.DecodedImageFormat?.Name == "WEBP" ||
                               image.Metadata.DecodedImageFormat?.Name == "GIF";

        // Calculate new dimensions maintaining aspect ratio
        int targetWidth = image.Width;
        int targetHeight = image.Height;

        if (maxWidth.HasValue && targetWidth > maxWidth.Value)
        {
            var ratio = (float)maxWidth.Value / targetWidth;
            targetWidth = maxWidth.Value;
            targetHeight = (int)(targetHeight * ratio);
        }

        if (maxHeight.HasValue && targetHeight > maxHeight.Value)
        {
            var ratio = (float)maxHeight.Value / targetHeight;
            targetHeight = maxHeight.Value;
            targetWidth = (int)(targetWidth * ratio);
        }

        // Resize if needed
        if (targetWidth != image.Width || targetHeight != image.Height)
        {
            image.Mutate(x => x.Resize(targetWidth, targetHeight, SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3));
        }

        // Choose format based on transparency
        using var outputStream = new MemoryStream();

        if (hasTransparency)
        {
            // Preserve transparency with PNG (for icons, logos, etc.)
            image.SaveAsPng(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestCompression
            });
            isPng = true;
        }
        else
        {
            // Use JPEG for photos (better compression)
            image.SaveAsJpeg(outputStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
            {
                Quality = quality
            });
            isPng = false;
        }

        return outputStream.ToArray();
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

    private static string[] ParseTagSlugs(string? tagsCsv)
    {
        if (string.IsNullOrWhiteSpace(tagsCsv))
        {
            return Array.Empty<string>();
        }

        return tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Distinct()
            .ToArray();
    }
}
