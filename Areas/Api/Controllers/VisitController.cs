using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;

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
    public VisitController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger)
        : base(dbContext, logger)
    {
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

            _logger.LogInformation("User {UserId} deleted {Count} visits", userId, visits.Count);

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
}
