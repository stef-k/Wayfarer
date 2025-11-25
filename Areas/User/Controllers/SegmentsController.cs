using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class SegmentsController : BaseController
{
    private new readonly ILogger<SegmentsController> _logger;
    private readonly ApplicationDbContext _db;

    public SegmentsController(ILogger<SegmentsController> logger, ApplicationDbContext dbContext) : base(logger,
        dbContext)
    {
        _logger = logger;
        _db = dbContext;
    }

    // Transport modes and their travel speed in km/h
    private static readonly Dictionary<string, double> ModeSpeedsKmh = new()
    {
        ["walk"] = 5,
        ["bicycle"] = 15,
        ["bike"] = 40,
        ["car"] = 60,
        ["bus"] = 35,
        ["train"] = 100,
        ["ferry"] = 30,
        ["boat"] = 25,
        ["flight"] = 800,
        ["helicopter"] = 200
    };

    // GET: /User/Segments/CreateOrUpdate?tripId=...
    [HttpGet]
    public async Task<IActionResult> CreateOrUpdate(Guid? segmentId, Guid tripId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var trip = await _db.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        if (trip == null) return NotFound();

        Segment segment;

        if (segmentId.HasValue)
        {
            segment = await _db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId && s.UserId == userId);
            if (segment == null) return NotFound();
            if (segment.RouteGeometry is { } geom && geom.NumPoints >= 2)
            {
                var coords = geom.Coordinates
                    .Select(c => new[] { c.Y, c.X }) // [lat, lon]
                    .ToList();

                ViewData["RouteJson"] = JsonSerializer.Serialize(coords);
            }
        }
        else
        {
            segment = new Segment
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                UserId = userId
            };
        }

        ViewData["Places"] = trip.Regions
            .SelectMany(r => r.Places ?? new List<Place>())
            .OrderBy(p => p.Name)
            .ToList();

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentFormPartial.cshtml", segment);
    }

    // POST: /User/Segments/CreateOrUpdate
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrUpdate(Segment model)
    {
        ModelState.Remove(nameof(model.UserId));
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        model.UserId = userId;

        // Convert EstimatedDurationMinutes from form (if present)
        if (Request.Form.TryGetValue("EstimatedDurationMinutes", out var durationStr) &&
            double.TryParse(durationStr, out var durationMin))
        {
            model.EstimatedDuration = TimeSpan.FromMinutes(durationMin);
        }

        // Auto-calculate EstimatedDuration if missing but distance and mode are present
        if (model.EstimatedDistanceKm.HasValue && !model.EstimatedDuration.HasValue)
        {
            if (ModeSpeedsKmh.TryGetValue(model.Mode?.ToLower() ?? "", out var speedKmh) && speedKmh > 0)
            {
                var hours = model.EstimatedDistanceKm.Value / speedKmh;
                model.EstimatedDuration = TimeSpan.FromHours(hours);
            }
        }

        // Remove navigation properties from validation
        ModelState.Remove(nameof(model.Trip));
        ModelState.Remove(nameof(model.FromPlace));
        ModelState.Remove(nameof(model.ToPlace));

        // Handle RouteJson → RouteGeometry
        if (Request.Form.TryGetValue("RouteJson", out var routeJsonStr) && !string.IsNullOrWhiteSpace(routeJsonStr))
        {
            try
            {
                var points = JsonSerializer.Deserialize<List<double[]>>(routeJsonStr.ToString());
                if (points != null && points.Count >= 2)
                {
                    var coords = points.Select(p => new Coordinate(p[1], p[0])).ToArray(); // [lon, lat]
                    model.RouteGeometry = new LineString(coords) { SRID = 4326 };
                }
            }
            catch (JsonException)
            {
                _logger.LogWarning("Invalid RouteJson ignored for Segment {SegmentId}", model.Id);
            }
        }
        else
        {
            model.RouteGeometry = null;
        }

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid model — exiting early");
            return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentFormPartial.cshtml", model);
        }
        else
        {
            _logger.LogInformation("Valid model — proceeding to save segment...");
        }

        var exists = await _dbContext.Segments
            .AnyAsync(s => s.Id == model.Id && s.UserId == userId);

        if (exists)
            _dbContext.Segments.Update(model);
        else
            _dbContext.Segments.Add(model);

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Re-fetch the full segment with related From/To place for display
        var segment = await _dbContext.Segments
            .Include(s => s.FromPlace!).ThenInclude(p => p.Region)
            .Include(s => s.ToPlace!).ThenInclude(p => p.Region)
            .FirstOrDefaultAsync(s => s.Id == model.Id && s.UserId == userId);

        if (segment == null)
        {
            _logger.LogError("Segment not found after save.");
            return Problem("Segment not found.");
        }

        if (segment.FromPlace == null || segment.ToPlace == null)
        {
            _logger.LogError("Segment {SegmentId} has null FromPlace or ToPlace.", segment.Id);
            return Problem("Segment references are incomplete.");
        }

        try
        {
            return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentItemPartial.cshtml", segment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🛑 Rendering failed for SegmentItemPartial.");
            return Problem("An error occurred while rendering the updated segment.");
        }
    }

    // POST: /User/Segments/Delete/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var segment = await _db.Segments.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (segment == null)
            return NotFound();

        _db.Segments.Remove(segment);
        await _db.SaveChangesAsync();

        // Return a marker for frontend removal
        return Json(new { deleted = id });
    }

    // POST: /User/Segments/Reorder
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder([FromBody] List<OrderDto> items)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var idToOrder = items.ToDictionary(i => i.Id, i => i.Order);

        var segments = await _db.Segments
            .Where(s => idToOrder.Keys.Contains(s.Id) && s.UserId == userId)
            .ToListAsync();

        foreach (var seg in segments)
            seg.DisplayOrder = idToOrder[seg.Id];

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET: /User/Segments/GetItemPartial?segmentId=...
    [HttpGet]
    public async Task<IActionResult> GetItemPartial(Guid segmentId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var segment = await _db.Segments
            .Include(s => s.FromPlace!).ThenInclude(p => p.Region)
            .Include(s => s.ToPlace!).ThenInclude(p => p.Region)
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.UserId == userId);

        if (segment == null)
            return NotFound();

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentItemPartial.cshtml", segment);
    }
    
    [HttpGet]
    public async Task<IActionResult> GetSegments(Guid tripId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var segments = await _db.Segments
            .Include(s => s.FromPlace!).ThenInclude(p => p.Region)
            .Include(s => s.ToPlace!).ThenInclude(p => p.Region)
            .Where(s => s.UserId == userId && s.TripId == tripId)
            .ToListAsync();

        var dtoList = segments.Select(s => new SegmentDto
        {
            Id = s.Id,
            Mode = s.Mode,
            EstimatedDistanceKm = s.EstimatedDistanceKm,
            EstimatedDuration = s.EstimatedDuration,
            Notes = s.Notes,
            RouteJson = s.RouteGeometry != null
                ? JsonSerializer.Serialize(
                    s.RouteGeometry.Coordinates.Select(c => new[] { c.Y, c.X })
                )
                : null,
            FromPlace = new PlaceDto
            {
                Id = s.FromPlace.Id,
                Name = s.FromPlace.Name,
                Location = s.FromPlace.Location
            },
            ToPlace = new PlaceDto
            {
                Id = s.ToPlace.Id,
                Name = s.ToPlace.Name,
                Location = s.ToPlace.Location
            }
        }).ToList();

        return Json(dtoList);
    }
}
