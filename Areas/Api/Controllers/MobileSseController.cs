using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Models.Options;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/mobile/sse")]
public class MobileSseController : MobileApiController
{
    private readonly SseService _sseService;
    private readonly IGroupTimelineService _timelineService;
    private readonly MobileSseOptions _options;

    public MobileSseController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IMobileCurrentUserAccessor userAccessor,
        SseService sseService,
        IGroupTimelineService timelineService,
        MobileSseOptions options)
        : base(dbContext, logger, userAccessor)
    {
        _sseService = sseService;
        _timelineService = timelineService;
        _options = options;
    }

    /// <summary>
    /// SSE endpoint for visit notifications.
    /// Broadcasts when the authenticated user's visit to a planned place is confirmed.
    /// Requires authentication via Bearer token (mobile) or cookie (webapp).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SSE stream with visit_started events.</returns>
    [HttpGet("visits")]
    public async Task<IActionResult> SubscribeToVisitsAsync(CancellationToken cancellationToken)
    {
        var (caller, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        await _sseService.SubscribeAsync(
            $"user-visits-{caller!.Id}",
            Response,
            cancellationToken,
            enableHeartbeat: true,
            heartbeatInterval: _options.HeartbeatInterval);
        return new EmptyResult();
    }

    /// <summary>
    /// Consolidated SSE endpoint for all group events (locations + membership changes).
    /// Replaces the separate group-location-update and group-membership-update streams.
    /// Requires authentication via Bearer token (mobile) or cookie (webapp).
    /// </summary>
    /// <param name="groupId">The group to subscribe to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SSE stream with typed events.</returns>
    [HttpGet("group/{groupId:guid}")]
    public async Task<IActionResult> SubscribeToGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var (caller, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var context = await _timelineService.BuildAccessContextAsync(groupId, caller!.Id, cancellationToken);
        if (context == null) return NotFound();
        if (!context.IsMember) return StatusCode(StatusCodes.Status403Forbidden);

        await _sseService.SubscribeAsync(
            $"group-{groupId}",
            Response,
            cancellationToken,
            enableHeartbeat: true,
            heartbeatInterval: _options.HeartbeatInterval);
        return new EmptyResult();
    }
}
