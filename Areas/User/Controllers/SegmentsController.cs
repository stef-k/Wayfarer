using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
public class SegmentsController : BaseController
{
    private readonly ILogger<PlacesController> _logger;
    private readonly ApplicationDbContext _dbContext;

    public SegmentsController(ILogger<PlacesController> logger, ApplicationDbContext dbContext) : base(logger, dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    // GET: /User/Segments/Create?tripId=...
    public async Task<IActionResult> Create(Guid tripId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
                .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        if (trip == null)
            return NotFound();

        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId
        };

        ViewData["Places"] = trip.Regions
            .SelectMany(r => r.Places ?? new List<Place>())
            .OrderBy(p => p.Name)
            .ToList();

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentFormPartial.cshtml", segment);
    }

    // GET: /User/Segments/Edit/{id}
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var segment = await _dbContext.Segments
            .Include(s => s.Trip)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (segment == null)
            return NotFound();

        var places = await _dbContext.Places
            .Where(p => p.UserId == userId && p.Region.TripId == segment.TripId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        ViewData["Places"] = places;

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentFormPartial.cshtml", segment);
    }

    // POST: /User/Segments/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Segment model)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        model.UserId = userId;
        
        // Convert EstimatedDurationMinutes from form
        if (Request.Form.TryGetValue("EstimatedDurationMinutes", out var durationStr) &&
            double.TryParse(durationStr, out var durationMin))
        {
            model.EstimatedDuration = TimeSpan.FromMinutes(durationMin);
        }

        
        ModelState.Remove(nameof(model.Trip));
        ModelState.Remove(nameof(model.FromPlace));
        ModelState.Remove(nameof(model.ToPlace));

        if (!ModelState.IsValid)
            return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentFormPartial.cshtml", model);

        var exists = await _dbContext.Segments
            .AnyAsync(s => s.Id == model.Id && s.UserId == userId);

        if (exists)
        {
            _dbContext.Segments.Update(model);
        }
        else
        {
            _dbContext.Segments.Add(model);
        }

        await _dbContext.SaveChangesAsync();

        var trip = await _dbContext.Trips
            .Include(t => t.Segments)
            .ThenInclude(s => s.FromPlace)
            .Include(t => t.Segments)
            .ThenInclude(s => s.ToPlace)
            .FirstOrDefaultAsync(t => t.Id == model.TripId && t.UserId == userId);

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentListPartial.cshtml", trip);
    }

    // POST: /User/Segments/Delete/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var segment = await _dbContext.Segments
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (segment == null)
            return NotFound();

        var tripId = segment.TripId;

        _dbContext.Segments.Remove(segment);
        await _dbContext.SaveChangesAsync();

        var trip = await _dbContext.Trips
            .Include(t => t.Segments)
            .ThenInclude(s => s.FromPlace)
            .Include(t => t.Segments)
            .ThenInclude(s => s.ToPlace)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        return PartialView("~/Areas/User/Views/Trip/Partials/_SegmentListPartial.cshtml", trip);
    }
}
