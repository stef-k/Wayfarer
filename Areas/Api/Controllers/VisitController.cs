using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// API controller for visit search, pagination, and bulk operations.
/// Provides endpoints for the Visit management UI.
/// </summary>
[Area("Api")]
[Route("api/[controller]")]
[ApiController]
public class VisitController : BaseApiController
{
    private readonly IApplicationSettingsService _settingsService;

    public VisitController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IApplicationSettingsService settingsService)
        : base(dbContext, logger)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Search visits with pagination and filtering.
    /// </summary>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="fromDate">Filter by arrived date from</param>
    /// <param name="toDate">Filter by arrived date to</param>
    /// <param name="tripId">Filter by trip ID</param>
    /// <param name="status">Filter by status (open/closed)</param>
    /// <param name="placeName">Filter by place name (contains)</param>
    /// <param name="regionName">Filter by region name (contains)</param>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        int page = 1,
        int pageSize = 20,
        string? fromDate = null,
        string? toDate = null,
        Guid? tripId = null,
        string? status = null,
        string? placeName = null,
        string? regionName = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            var query = _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId)
                .AsQueryable();

            // Apply filters
            if (!string.IsNullOrEmpty(fromDate) && DateTime.TryParse(fromDate, out var fromDateTime))
            {
                var fromUtc = DateTime.SpecifyKind(fromDateTime.Date, DateTimeKind.Utc);
                query = query.Where(v => v.ArrivedAtUtc >= fromUtc);
            }

            if (!string.IsNullOrEmpty(toDate) && DateTime.TryParse(toDate, out var toDateTime))
            {
                var toUtc = DateTime.SpecifyKind(toDateTime.Date.AddDays(1), DateTimeKind.Utc);
                query = query.Where(v => v.ArrivedAtUtc < toUtc);
            }

            if (tripId.HasValue)
            {
                query = query.Where(v => v.TripIdSnapshot == tripId.Value);
            }

            if (!string.IsNullOrEmpty(status))
            {
                if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(v => v.EndedAtUtc == null);
                }
                else if (status.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(v => v.EndedAtUtc != null);
                }
            }

            if (!string.IsNullOrEmpty(placeName))
            {
                var loweredPlaceName = placeName.ToLower();
                query = query.Where(v => v.PlaceNameSnapshot != null && v.PlaceNameSnapshot.ToLower().Contains(loweredPlaceName));
            }

            if (!string.IsNullOrEmpty(regionName))
            {
                var loweredRegionName = regionName.ToLower();
                query = query.Where(v => v.RegionNameSnapshot != null && v.RegionNameSnapshot.ToLower().Contains(loweredRegionName));
            }

            // Get total count before pagination
            var totalItems = await query.CountAsync();

            // Apply ordering and pagination - fetch entities first
            var visitEntities = await query
                .OrderByDescending(v => v.ArrivedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Project to DTOs in-memory (avoids EF Core geography translation issues)
            var visits = visitEntities.Select(v => new
            {
                v.Id,
                v.PlaceId,
                v.PlaceNameSnapshot,
                v.TripIdSnapshot,
                v.TripNameSnapshot,
                v.RegionNameSnapshot,
                v.ArrivedAtUtc,
                v.EndedAtUtc,
                v.LastSeenAtUtc,
                Latitude = v.PlaceLocationSnapshot?.Y,
                Longitude = v.PlaceLocationSnapshot?.X,
                v.IconNameSnapshot,
                v.MarkerColorSnapshot,
                v.NotesHtml
            }).ToList();

            return Ok(new
            {
                success = true,
                data = visits,
                totalItems,
                page,
                pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching visits for user {UserId}", userId);
            return StatusCode(500, new { success = false, message = "An error occurred while searching visits." });
        }
    }

    /// <summary>
    /// Get list of trips that have visits (for filter dropdown).
    /// </summary>
    [HttpGet("trips")]
    public async Task<IActionResult> GetTripsWithVisits()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            var trips = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId)
                .GroupBy(v => new { v.TripIdSnapshot, v.TripNameSnapshot })
                .Select(g => new
                {
                    Id = g.Key.TripIdSnapshot,
                    Name = g.Key.TripNameSnapshot
                })
                .OrderBy(t => t.Name)
                .ToListAsync();

            return Ok(trips);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trips for user {UserId}", userId);
            return StatusCode(500, new { success = false, message = "An error occurred." });
        }
    }

    /// <summary>
    /// Bulk delete visits.
    /// </summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        if (request?.VisitIds == null || request.VisitIds.Length == 0)
            return BadRequest(new { success = false, message = "No visit IDs provided." });

        try
        {
            var visits = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId && request.VisitIds.Contains(v.Id))
                .ToListAsync();

            if (visits.Count == 0)
            {
                return NotFound(new { success = false, message = "No matching visits found." });
            }

            _dbContext.PlaceVisitEvents.RemoveRange(visits);
            await _dbContext.SaveChangesAsync();

            _logger.LogDebug("User {UserId} deleted {Count} visits", userId, visits.Count);

            return Ok(new
            {
                success = true,
                deletedCount = visits.Count,
                message = $"Successfully deleted {visits.Count} visit(s)."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk deleting visits for user {UserId}", userId);
            return StatusCode(500, new { success = false, message = "An error occurred while deleting visits." });
        }
    }

    /// <summary>
    /// Request model for bulk delete operation.
    /// </summary>
    public class BulkDeleteRequest
    {
        /// <summary>Array of visit IDs to delete.</summary>
        public Guid[] VisitIds { get; set; } = Array.Empty<Guid>();
    }

    /// <summary>
    /// Get location counts for multiple visits in batch.
    /// Used to lazy-load counts after the main search results are displayed.
    /// </summary>
    /// <param name="request">Request containing visit IDs to count locations for.</param>
    [HttpPost("location-counts")]
    public async Task<IActionResult> GetLocationCounts([FromBody] LocationCountsRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        if (request?.VisitIds == null || request.VisitIds.Length == 0)
            return Ok(new { success = true, counts = new Dictionary<Guid, int>() });

        try
        {
            // Get visits for the requested IDs (verify ownership)
            var visits = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId && request.VisitIds.Contains(v.Id))
                .ToListAsync();

            var settings = _settingsService.GetSettings();
            var radiusMeters = settings.VisitedMaxSearchRadiusMeters;

            var counts = await GetLocationCountsForVisitsAsync(userId, visits, radiusMeters);

            return Ok(new { success = true, counts });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting location counts for user {UserId}", userId);
            return StatusCode(500, new { success = false, message = "An error occurred." });
        }
    }

    /// <summary>
    /// Request model for batch location counts.
    /// </summary>
    public class LocationCountsRequest
    {
        /// <summary>Array of visit IDs to get location counts for.</summary>
        public Guid[] VisitIds { get; set; } = Array.Empty<Guid>();
    }

    /// <summary>
    /// Get locations relevant to a visit (within time window and proximity radius).
    /// Returns locations that fall within the visit's time window and are near the place location.
    /// </summary>
    /// <param name="visitId">The visit ID</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    [HttpGet("{visitId:guid}/locations")]
    public async Task<IActionResult> GetVisitLocations(
        Guid visitId,
        int page = 1,
        int pageSize = 10)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            // Get the visit and verify ownership
            var visit = await _dbContext.PlaceVisitEvents
                .FirstOrDefaultAsync(v => v.Id == visitId && v.UserId == userId);

            if (visit == null)
                return NotFound(new { success = false, message = "Visit not found." });

            if (visit.PlaceLocationSnapshot == null)
                return Ok(new { success = true, data = Array.Empty<object>(), totalItems = 0, page, pageSize });

            // Get search radius from settings
            var settings = _settingsService.GetSettings();
            var radiusMeters = settings.VisitedMaxSearchRadiusMeters;

            // Determine end time: use EndedAtUtc if closed, otherwise LastSeenAtUtc
            var endTime = visit.EndedAtUtc ?? visit.LastSeenAtUtc;

            // Build query: locations within time window and proximity
            // Use server-side Timestamp for comparison since visit timestamps are also server-side UTC
            var query = _dbContext.Locations
                .Include(l => l.ActivityType)
                .Where(l => l.UserId == userId)
                .Where(l => l.Timestamp >= visit.ArrivedAtUtc)
                .Where(l => l.Timestamp <= endTime)
                .Where(l => l.Coordinates.IsWithinDistance(visit.PlaceLocationSnapshot, radiusMeters))
                .OrderBy(l => l.Timestamp);

            // Get total count
            var totalItems = await query.CountAsync();

            // Fetch paginated location entities first
            var locationEntities = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Project to DTOs in-memory (avoids EF Core geography translation issues)
            var locations = locationEntities.Select(l => new
            {
                l.Id,
                l.LocalTimestamp,
                l.TimeZoneId,
                Latitude = l.Coordinates.Y,
                Longitude = l.Coordinates.X,
                l.Accuracy,
                l.Speed,
                Activity = l.ActivityType?.Name,
                l.Address
            }).ToList();

            return Ok(new
            {
                success = true,
                data = locations,
                totalItems,
                page,
                pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting locations for visit {VisitId}, user {UserId}", visitId, userId);
            return StatusCode(500, new { success = false, message = "An error occurred while retrieving locations." });
        }
    }

    /// <summary>
    /// Get location counts for multiple visits sequentially.
    /// Note: DbContext is not thread-safe, so we cannot run queries in parallel.
    /// </summary>
    private async Task<Dictionary<Guid, int>> GetLocationCountsForVisitsAsync(
        string userId,
        List<PlaceVisitEvent> visits,
        int radiusMeters)
    {
        var counts = new Dictionary<Guid, int>();

        foreach (var v in visits)
        {
            if (v.PlaceLocationSnapshot == null)
            {
                counts[v.Id] = 0;
                continue;
            }

            var endTime = v.EndedAtUtc ?? v.LastSeenAtUtc;
            var count = await _dbContext.Locations
                .Where(l => l.UserId == userId)
                .Where(l => l.Timestamp >= v.ArrivedAtUtc)
                .Where(l => l.Timestamp <= endTime)
                .Where(l => l.Coordinates.IsWithinDistance(v.PlaceLocationSnapshot, radiusMeters))
                .CountAsync();

            counts[v.Id] = count;
        }

        return counts;
    }
}
