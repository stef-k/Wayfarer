using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// SSE controller providing both legacy generic streams and the new authenticated group stream.
/// </summary>
[Area("Api")]
[Route("api/sse")]
public class SseController : Controller
{
    private readonly SseService _sse;
    private readonly ApplicationDbContext _db;
    private readonly IGroupTimelineService _timelineService;
    private readonly MobileSseOptions _options;

    public SseController(
        SseService sse,
        ApplicationDbContext db,
        IGroupTimelineService timelineService,
        MobileSseOptions options)
    {
        _sse = sse;
        _db = db;
        _timelineService = timelineService;
        _options = options;
    }

    /// <summary>
    /// Legacy generic SSE stream endpoint. Routes to channel based on type/id.
    /// Note: No authentication - maintained for backwards compatibility with non-group streams.
    /// </summary>
    [HttpGet("stream/{type}/{id}")]
    public async Task Stream(string type, string id, CancellationToken ct)
    {
        var channel = $"{type}-{id}";
        await _sse.SubscribeAsync(channel, Response, ct);
    }

    /// <summary>
    /// Consolidated SSE endpoint for all group events (locations + membership changes).
    /// Requires authentication via cookie (webapp) or Bearer token (mobile).
    /// </summary>
    /// <param name="groupId">The group to subscribe to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>SSE stream with typed events.</returns>
    [Authorize]
    [HttpGet("group/{groupId:guid}")]
    public async Task<IActionResult> SubscribeToGroupAsync(Guid groupId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var context = await _timelineService.BuildAccessContextAsync(groupId, userId, ct);
        if (context == null)
            return NotFound();
        if (!context.IsMember)
            return Forbid();

        await _sse.SubscribeAsync(
            $"group-{groupId}",
            Response,
            ct,
            enableHeartbeat: true,
            heartbeatInterval: _options.HeartbeatInterval);
        return new EmptyResult();
    }
}