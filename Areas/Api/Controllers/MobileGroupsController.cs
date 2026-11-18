using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
///     Mobile endpoints for retrieving group data and locations.
/// </summary>
[Area("Api")]
[Route("api/mobile/groups")]
public class MobileGroupsController : MobileApiController
{
    private readonly IUserColorService _colorService;
    private readonly IGroupTimelineService _timelineService;

    public MobileGroupsController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        IMobileCurrentUserAccessor userAccessor,
        IUserColorService colorService,
        IGroupTimelineService timelineService)
        : base(dbContext, logger, userAccessor)
    {
        _colorService = colorService;
        _timelineService = timelineService;
    }

    /// <summary>Returns the released collection shape from one authority and Group snapshot.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? scope, CancellationToken cancellationToken)
    {
        BrowserResponsePolicyMiddleware.RequirePrivateResponse(HttpContext);
        await using var snapshot = await GroupRead.BeginAsync(DbContext, cancellationToken);
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var normalizedScope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        var memberships = DbContext.GroupMembers.Where(m => m.UserId == user!.Id
            && m.Status == GroupMember.MembershipStatuses.Active);

        switch (normalizedScope)
        {
            case "managed":
                memberships = memberships.Where(m => m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager);
                break;
            case "joined":
                memberships = memberships.Where(m => m.Role == GroupMember.Roles.Member);
                break;
            case "all":
            case "":
                break;
            default:
                return BadRequest(new { message = "Unsupported scope value." });
        }

        var rows = await (from m in memberships
            join g in DbContext.Groups on m.GroupId equals g.Id
            where !g.IsArchived
            select new
            {
                g.Id, g.Name, g.Description, g.GroupType, g.OrgPeerVisibilityEnabled, m.Role,
                MemberCount = DbContext.GroupMembers.Count(peer => peer.GroupId == g.Id
                    && peer.Status == GroupMember.MembershipStatuses.Active)
            }).ToListAsync(cancellationToken);
        var payload = rows.Select(g =>
        {
            var isOrg = string.Equals(g.GroupType, "Organization", StringComparison.OrdinalIgnoreCase);
            return new MobileGroupSummaryDto
            {
                Id = g.Id, Name = g.Name, Description = g.Description, GroupType = g.GroupType, MemberCount = g.MemberCount,
                IsOwner = g.Role == GroupMember.Roles.Owner,
                IsManager = g.Role is GroupMember.Roles.Owner or GroupMember.Roles.Manager,
                IsMember = g.Role == GroupMember.Roles.Member,
                OrgPeerVisibilityEnabled = isOrg && g.OrgPeerVisibilityEnabled,
                // Own opt-out does not disable access to willing peers.
                HasOrgPeerVisibilityAccess = !isOrg || g.OrgPeerVisibilityEnabled
            };
        }).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(payload);
    }

    [HttpGet("{groupId:guid}/members")]
    public async Task<IActionResult> Members(Guid groupId, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var callerMembership = await DbContext.GroupMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m =>
                    m.GroupId == groupId &&
                    m.UserId == user!.Id &&
                    m.Status == GroupMember.MembershipStatuses.Active,
                cancellationToken);

        if (callerMembership == null) return StatusCode(StatusCodes.Status403Forbidden);

        var group = await DbContext.Groups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsArchived, cancellationToken);
        if (group == null) return NotFound();

        return Ok(await GroupRoster.ReadAsync(DbContext, groupId, user!.Id, mobile: true, ct: cancellationToken, colors: _colorService));
    }

    [HttpPost("{groupId:guid}/locations/latest")]
    public async Task<IActionResult> Latest(Guid groupId, [FromBody] GroupLocationsLatestRequest? request,
        CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var context = await _timelineService.BuildAccessContextAsync(groupId, user!.Id, cancellationToken);
        if (context == null) return NotFound();
        if (!context.IsMember) return StatusCode(StatusCodes.Status403Forbidden);

        var results =
            await _timelineService.GetLatestLocationsAsync(context, request?.IncludeUserIds, cancellationToken);
        return Ok(results);
    }

    [HttpPost("{groupId:guid}/locations/query")]
    public async Task<IActionResult> Query(Guid groupId, [FromBody] GroupLocationsQueryRequest request,
        CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var context = await _timelineService.BuildAccessContextAsync(groupId, user!.Id, cancellationToken);
        if (context == null) return NotFound();
        if (!context.IsMember) return StatusCode(StatusCodes.Status403Forbidden);

        var queryResult = await _timelineService.QueryLocationsAsync(context, request, cancellationToken);
        var response = new GroupLocationsQueryResponse
        {
            TotalItems = queryResult.TotalItems,
            ReturnedItems = queryResult.Results.Count,
            PageSize = queryResult.PageSize,
            HasMore = queryResult.HasMore,
            NextPageToken = queryResult.NextPageToken,
            IsTruncated = queryResult.IsTruncated,
            Results = queryResult.Results
        };

        return Ok(response);
    }

    /// <summary>Dispatches observed episode writes and preserves released scalar binding and failures.</summary>
    [HttpPost("{groupId}/peer-visibility")]
    public async Task<IActionResult> SetPeerVisibility(string groupId, [FromBody] OrgPeerVisibilityAccessRequest request,
        CancellationToken cancellationToken)
    {
        var reviewed = request.MembershipRef.ValueKind != JsonValueKind.Undefined;
        if (!Guid.TryParse(groupId, out var id)) return reviewed ? BadRequest() : NotFound();

        string? reference = null;
        if (reviewed)
        {
            if (request.MembershipRef.ValueKind != JsonValueKind.String || !request.HasDisabled)
                return BadRequest();
            reference = request.MembershipRef.GetString();
            if (string.IsNullOrWhiteSpace(reference)) return BadRequest();
        }

        var user = reviewed
            ? await MobileBearer.ResolveAsync(HttpContext, DbContext, cancellationToken)
            : await GetCurrentUserAsync(cancellationToken);
        if (user is not { IsActive: true })
            return Unauthorized(new { message = "Invalid or missing API token." });

        try
        {
            await GroupPersonalSharingWrite.SetAsync(DbContext, id, user.Id, request.Disabled, reference, audit: false,
                cancellationToken, token => IncomingApiTokenResolver.ForRequest(HttpContext, DbContext)
                    .RevalidateMutationAsync(HttpContext, user.Id, token));
        }
        catch (GroupRefusal refusal)
        {
            if (refusal.Status == 401) return Unauthorized(new { message = "Invalid or missing API token." });
            if (reviewed) return StatusCode(refusal.Status, new { code = refusal.Code });
            return refusal.Status == 400
                ? BadRequest(new { message = "Personal sharing is unavailable for this group policy" })
                : StatusCode(refusal.Status);
        }

        Logger.LogInformation("User {UserId} set peer visibility in group {GroupId}: disabled={Disabled}",
            user.Id, id, request.Disabled);
        var sseService = HttpContext.RequestServices.GetRequiredService<SseService>();
        await sseService.BroadcastGroupHintAsync(id,
            JsonSerializer.Serialize(GroupSseEventDto.VisibilityChanged(user.Id, request.Disabled)));
        return Ok(new { disabled = request.Disabled });
    }
}
