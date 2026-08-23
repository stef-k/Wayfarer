using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public SseController(
        SseService sse,
        ApplicationDbContext db,
        IGroupTimelineService timelineService,
        MobileSseOptions options,
        IServiceScopeFactory scopeFactory)
    {
        _sse = sse;
        _db = db;
        _timelineService = timelineService;
        _options = options;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Legacy generic SSE stream endpoint. Routes to channel based on type/id.
    /// Note: No authentication - maintained for backwards compatibility with non-group streams.
    /// </summary>
    [HttpGet("stream/{type}/{id}")]
    public async Task Stream(string type, string id, CancellationToken ct)
    {
        if (type.Equals("import", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("import-", StringComparison.OrdinalIgnoreCase)
            || type.Equals("enrichment", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("enrichment-", StringComparison.OrdinalIgnoreCase))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (type == "location-update")
        {
            var user = await _db.Users
                .AsNoTracking()
                .Where(user => user.UserName == id)
                .Select(user => new { user.IsTimelinePublic, user.PublicTimelineTimeThreshold })
                .FirstOrDefaultAsync(ct);
            if (user is null || PublicTimelineEligibilityResolver.Resolve(user.IsTimelinePublic, user.PublicTimelineTimeThreshold) is not { IsEffectivelyPublic: true, IsLive: true })
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _sse.SubscribeAsync(
                $"{type}-{id}",
                Response,
                ct,
                deliveryLease: cancellationToken => AcquirePublicLocationDeliveryLeaseAsync(id, cancellationToken));
            return;
        }

        var channel = $"{type}-{id}";
        await _sse.SubscribeAsync(channel, Response, ct);
    }

    /// <summary>Subscribes only to the authenticated caller's content-free import progress channel.</summary>
    [Authorize]
    [HttpGet("import")]
    public async Task<IActionResult> SubscribeToImportAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        await _sse.SubscribeAsync($"import-{userId}", Response, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Acquires the per-timeline lease only while fresh persisted state permits public live delivery.
    /// </summary>
    private async Task<IAsyncDisposable?> AcquirePublicLocationDeliveryLeaseAsync(string username, CancellationToken cancellationToken)
    {
        IAsyncDisposable deliveryLease = await PublicTimelineDeliveryLock.AcquireAsync(username, cancellationToken);
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
                .AsNoTracking()
                .Where(candidate => candidate.UserName == username)
                .Select(candidate => new { candidate.IsTimelinePublic, candidate.PublicTimelineTimeThreshold })
                .FirstOrDefaultAsync(cancellationToken);
            if (user is null || PublicTimelineEligibilityResolver.Resolve(user.IsTimelinePublic, user.PublicTimelineTimeThreshold) is not { IsEffectivelyPublic: true, IsLive: true })
            {
                await deliveryLease.DisposeAsync();
                return null;
            }

            return deliveryLease;
        }
        catch
        {
            await deliveryLease.DisposeAsync();
            throw;
        }
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
