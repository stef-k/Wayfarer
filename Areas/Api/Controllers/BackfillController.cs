using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// API controller for visit backfill operations.
/// Allows users to analyze location history and create/clean visit records.
/// </summary>
[Area("Api")]
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class BackfillController : BaseApiController
{
    private readonly IVisitBackfillService _backfillService;

    public BackfillController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IVisitBackfillService backfillService)
        : base(dbContext, logger)
    {
        _backfillService = backfillService;
    }

    /// <summary>
    /// Get backfill analysis metadata for progress feedback.
    /// </summary>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="fromDate">Optional start date filter (yyyy-MM-dd).</param>
    /// <param name="toDate">Optional end date filter (yyyy-MM-dd).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("info/{tripId:guid}")]
    public async Task<IActionResult> Info(
        Guid tripId,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            DateOnly? from = null;
            DateOnly? to = null;

            if (!string.IsNullOrEmpty(fromDate))
            {
                if (!DateOnly.TryParse(fromDate, out var parsedFrom))
                    return BadRequest(new { success = false, message = $"Invalid fromDate format: '{fromDate}'. Use yyyy-MM-dd." });
                from = parsedFrom;
            }

            if (!string.IsNullOrEmpty(toDate))
            {
                if (!DateOnly.TryParse(toDate, out var parsedTo))
                    return BadRequest(new { success = false, message = $"Invalid toDate format: '{toDate}'. Use yyyy-MM-dd." });
                to = parsedTo;
            }

            var result = await _backfillService.GetInfoAsync(userId, tripId, from, to, cancellationToken);

            return Ok(new { success = true, data = result });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting backfill info for trip {TripId}, user {UserId}", tripId, userId);
            return StatusCode(500, new { success = false, message = "An error occurred." });
        }
    }

    /// <summary>
    /// Preview backfill analysis for a trip.
    /// Analyzes location history against trip places to find potential visits.
    /// </summary>
    /// <param name="tripId">The trip ID to analyze.</param>
    /// <param name="fromDate">Optional start date filter (yyyy-MM-dd).</param>
    /// <param name="toDate">Optional end date filter (yyyy-MM-dd).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("preview/{tripId:guid}")]
    public async Task<IActionResult> Preview(
        Guid tripId,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            DateOnly? from = null;
            DateOnly? to = null;

            // Validate date formats if provided
            if (!string.IsNullOrEmpty(fromDate))
            {
                if (!DateOnly.TryParse(fromDate, out var parsedFrom))
                    return BadRequest(new { success = false, message = $"Invalid fromDate format: '{fromDate}'. Use yyyy-MM-dd." });
                from = parsedFrom;
            }

            if (!string.IsNullOrEmpty(toDate))
            {
                if (!DateOnly.TryParse(toDate, out var parsedTo))
                    return BadRequest(new { success = false, message = $"Invalid toDate format: '{toDate}'. Use yyyy-MM-dd." });
                to = parsedTo;
            }

            var result = await _backfillService.PreviewAsync(userId, tripId, from, to, cancellationToken);

            return Ok(new { success = true, data = result });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during backfill preview for trip {TripId}, user {UserId}", tripId, userId);
            return StatusCode(500, new { success = false, message = "An error occurred during analysis." });
        }
    }

    /// <summary>
    /// Apply backfill changes - create new visits and delete stale visits.
    /// </summary>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="request">The apply request with visits to create and delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("apply/{tripId:guid}")]
    public async Task<IActionResult> Apply(
        Guid tripId,
        [FromBody] BackfillApplyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        if (request == null)
            return BadRequest(new { success = false, message = "Request body is required." });

        try
        {
            var result = await _backfillService.ApplyAsync(userId, tripId, request, cancellationToken);

            if (!result.Success)
                return NotFound(new { success = false, message = result.Message });

            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying backfill for trip {TripId}, user {UserId}", tripId, userId);
            return StatusCode(500, new { success = false, message = "An error occurred while applying changes." });
        }
    }

    /// <summary>
    /// Clear all visits for a trip.
    /// </summary>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("clear/{tripId:guid}")]
    public async Task<IActionResult> Clear(
        Guid tripId,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, message = "Unauthorized." });

        try
        {
            var result = await _backfillService.ClearVisitsAsync(userId, tripId, cancellationToken);

            if (!result.Success)
                return NotFound(new { success = false, message = result.Message });

            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing visits for trip {TripId}, user {UserId}", tripId, userId);
            return StatusCode(500, new { success = false, message = "An error occurred while clearing visits." });
        }
    }
}
