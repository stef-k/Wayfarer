using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Mobile API controller for visit-related queries.
/// Provides endpoints for polling visit events when SSE is unavailable (e.g., background mode).
/// </summary>
[Area("Api")]
[Route("api/mobile/visits")]
public class MobileVisitsController : MobileApiController
{
    /// <summary>
    /// Maximum allowed value for the 'since' parameter in seconds.
    /// </summary>
    private const int MaxSinceSeconds = 300;

    /// <summary>
    /// Default value for the 'since' parameter in seconds.
    /// </summary>
    private const int DefaultSinceSeconds = 30;

    public MobileVisitsController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IMobileCurrentUserAccessor userAccessor)
        : base(dbContext, logger, userAccessor)
    {
    }

    /// <summary>
    /// Gets recent visit events for the authenticated user.
    /// Use this endpoint to poll for visits when SSE is unavailable (e.g., mobile background mode).
    /// Returns visits that started within the specified time window.
    /// </summary>
    /// <param name="since">
    /// Time window in seconds to look back for visits (default: 30, max: 300).
    /// Returns visits where LastSeenAtUtc >= (now - since seconds).
    /// Uses LastSeenAtUtc because it reflects when the visit was confirmed/created,
    /// whereas ArrivedAtUtc is the first hit time which may predate confirmation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of recent visit events in the same format as SSE broadcasts.</returns>
    /// <response code="200">Returns the list of recent visits (may be empty).</response>
    /// <response code="401">Invalid or missing API token.</response>
    [HttpGet("recent")]
    [ProducesResponseType(typeof(RecentVisitsResponse), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> GetRecentVisitsAsync(
        [FromQuery] int since = DefaultSinceSeconds,
        CancellationToken cancellationToken = default)
    {
        var (caller, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        // Clamp 'since' to valid range
        var clampedSince = Math.Clamp(since, 1, MaxSinceSeconds);
        var cutoffUtc = DateTime.UtcNow.AddSeconds(-clampedSince);

        var visitEntities = await DbContext.PlaceVisitEvents
            .Where(v => v.UserId == caller!.Id && v.LastSeenAtUtc >= cutoffUtc)
            .OrderByDescending(v => v.LastSeenAtUtc)
            .ToListAsync(cancellationToken);

        // Map to DTO using the same factory method as SSE broadcasts
        var visits = visitEntities
            .Select(VisitSseEventDto.FromVisitEvent)
            .ToList();

        return Ok(new RecentVisitsResponse
        {
            Success = true,
            Visits = visits
        });
    }
}

/// <summary>
/// Response model for the recent visits endpoint.
/// </summary>
public sealed class RecentVisitsResponse
{
    /// <summary>
    /// Indicates whether the request was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// List of recent visit events, in the same format as SSE broadcasts.
    /// </summary>
    public IReadOnlyList<VisitSseEventDto> Visits { get; init; } = Array.Empty<VisitSseEventDto>();
}
