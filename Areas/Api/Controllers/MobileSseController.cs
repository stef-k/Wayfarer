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

    [HttpGet("location-update/{userName}")]
    public async Task<IActionResult> SubscribeToUserAsync(string userName, CancellationToken cancellationToken)
    {
        var (caller, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        if (!await CanViewUserAsync(caller!, userName, cancellationToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        await _sseService.SubscribeAsync(
            $"location-update-{userName}",
            Response,
            cancellationToken,
            enableHeartbeat: true,
            heartbeatInterval: _options.HeartbeatInterval);
        return new EmptyResult();
    }

    [HttpGet("group-location-update/{groupId:guid}")]
    public async Task<IActionResult> SubscribeToGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var (caller, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var context = await _timelineService.BuildAccessContextAsync(groupId, caller!.Id, cancellationToken);
        if (context == null) return NotFound();
        if (!context.IsMember) return StatusCode(StatusCodes.Status403Forbidden);

        await _sseService.SubscribeAsync(
            $"group-location-update-{groupId}",
            Response,
            cancellationToken,
            enableHeartbeat: true,
            heartbeatInterval: _options.HeartbeatInterval);
        return new EmptyResult();
    }

    private async Task<bool> CanViewUserAsync(ApplicationUser caller, string targetUserName, CancellationToken cancellationToken)
    {
        if (string.Equals(caller.UserName, targetUserName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetUserName))
        {
            return false;
        }

        var normalizedTarget = targetUserName.Trim();
        var target = await DbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.ToLower() == normalizedTarget.ToLower(), cancellationToken);

        if (target == null)
        {
            return false;
        }

        var sharedGroups = await (from callerMember in DbContext.GroupMembers
                                  join targetMember in DbContext.GroupMembers on callerMember.GroupId equals targetMember.GroupId
                                  join grp in DbContext.Groups on callerMember.GroupId equals grp.Id
                                  where callerMember.UserId == caller.Id
                                        && callerMember.Status == GroupMember.MembershipStatuses.Active
                                        && targetMember.UserId == target.Id
                                        && targetMember.Status == GroupMember.MembershipStatuses.Active
                                        && !grp.IsArchived
                                  select new
                                  {
                                      grp.GroupType,
                                      targetMember.OrgPeerVisibilityAccessDisabled
                                  }).ToListAsync(cancellationToken);

        foreach (var entry in sharedGroups)
        {
            if (!string.Equals(entry.GroupType, "Friends", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!entry.OrgPeerVisibilityAccessDisabled)
            {
                return true;
            }
        }

        return false;
    }
}
