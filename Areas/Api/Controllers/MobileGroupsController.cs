using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? scope, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var normalizedScope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        var userId = user!.Id;

        var userMemberships = await DbContext.GroupMembers
            .Where(m => m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => new
            {
                m.GroupId,
                m.Role,
                m.OrgPeerVisibilityAccessDisabled
            })
            .ToListAsync(cancellationToken);

        var candidateGroupIds = new HashSet<Guid>();

        async Task IncludeOwnedAsync()
        {
            var owned = await DbContext.Groups
                .Where(g => g.OwnerUserId == userId && !g.IsArchived)
                .Select(g => g.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in owned) candidateGroupIds.Add(id);
        }

        switch (normalizedScope)
        {
            case "managed":
                foreach (var membership in userMemberships)
                    if (membership.Role == GroupMember.Roles.Owner || membership.Role == GroupMember.Roles.Manager)
                        candidateGroupIds.Add(membership.GroupId);

                await IncludeOwnedAsync();
                break;
            case "joined":
                foreach (var membership in userMemberships.Where(m => m.Role == GroupMember.Roles.Member))
                    candidateGroupIds.Add(membership.GroupId);
                break;
            case "all":
            case "":
                foreach (var membership in userMemberships) candidateGroupIds.Add(membership.GroupId);
                await IncludeOwnedAsync();
                break;
            default:
                return BadRequest(new { message = "Unsupported scope value." });
        }

        if (candidateGroupIds.Count == 0) return Ok(Array.Empty<MobileGroupSummaryDto>());

        var groups = await DbContext.Groups
            .Where(g => candidateGroupIds.Contains(g.Id) && !g.IsArchived)
            .AsNoTracking()
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Description,
                g.GroupType,
                g.OrgPeerVisibilityEnabled,
                g.OwnerUserId
            })
            .ToListAsync(cancellationToken);

        var memberCounts = await DbContext.GroupMembers
            .Where(m => candidateGroupIds.Contains(m.GroupId) && m.Status == GroupMember.MembershipStatuses.Active)
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var memberCountLookup = memberCounts.ToDictionary(x => x.GroupId, x => x.Count);
        var membershipLookup = userMemberships
            .GroupBy(x => x.GroupId)
            .ToDictionary(g => g.Key, g => g.First());

        var payload = groups
            .Select(g =>
            {
                membershipLookup.TryGetValue(g.Id, out var membership);
                var isOwner = string.Equals(g.OwnerUserId, userId, StringComparison.Ordinal);
                var isManager = membership?.Role == GroupMember.Roles.Manager;
                var isMember = membership?.Role == GroupMember.Roles.Member;
                var isOrg = string.Equals(g.GroupType, "Organization", StringComparison.OrdinalIgnoreCase);
                var hasOrgPeerVisibilityAccess = !isOrg ||
                                                 (g.OrgPeerVisibilityEnabled && (membership == null ||
                                                     !membership.OrgPeerVisibilityAccessDisabled));

                return new MobileGroupSummaryDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    GroupType = g.GroupType,
                    OrgPeerVisibilityEnabled = g.OrgPeerVisibilityEnabled,
                    MemberCount = memberCountLookup.TryGetValue(g.Id, out var count) ? count : 0,
                    IsOwner = isOwner,
                    IsManager = isOwner || isManager,
                    IsMember = isMember,
                    HasOrgPeerVisibilityAccess = hasOrgPeerVisibilityAccess
                };
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        var members = await (from m in DbContext.GroupMembers
                where m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active
                join u in DbContext.Users on m.UserId equals u.Id
                select new { Member = m, User = u })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var results = members
            .Select(x =>
            {
                var username = x.User.UserName ?? string.Empty;
                var isSelf = string.Equals(x.User?.Id, user!.Id, StringComparison.Ordinal);
                var color = _colorService.GetColorHex(username);

                return new GroupMemberDto
                {
                    UserId = x.User?.Id ?? string.Empty,
                    UserName = username,
                    DisplayName = x.User?.DisplayName ?? string.Empty,
                    GroupRole = x.Member.Role,
                    Status = x.Member.Status,
                    ColorHex = color,
                    IsSelf = isSelf,
                    SseChannel = string.IsNullOrWhiteSpace(username) ? null : $"location-update-{username}",
                    OrgPeerVisibilityAccessDisabled = x.Member.OrgPeerVisibilityAccessDisabled
                };
            })
            .OrderBy(m => m.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(results);
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

    /// <summary>
    ///     Set peer visibility access for the current user in a Friends group
    ///     POST /api/mobile/groups/{groupId}/peer-visibility
    /// </summary>
    [HttpPost("{groupId:guid}/peer-visibility")]
    public async Task<IActionResult> SetPeerVisibility(Guid groupId, [FromBody] OrgPeerVisibilityAccessRequest request,
        CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;

        var group = await DbContext.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group == null) return NotFound();

        var member = await DbContext.GroupMembers.FirstOrDefaultAsync(
            m => m.GroupId == groupId && m.UserId == user!.Id && m.Status == GroupMember.MembershipStatuses.Active,
            cancellationToken);

        if (member == null) return StatusCode(StatusCodes.Status403Forbidden);

        member.OrgPeerVisibilityAccessDisabled = request.Disabled;
        await DbContext.SaveChangesAsync(cancellationToken);

        Logger.LogInformation($"User {user?.Id} set peer visibility in group {groupId}: disabled={request.Disabled}");

        // Broadcast visibility change to all group members via SSE (consolidated group channel)
        var sseService = HttpContext.RequestServices.GetRequiredService<SseService>();
        await sseService.BroadcastAsync(
            $"group-{groupId}",
            JsonSerializer.Serialize(GroupSseEventDto.VisibilityChanged(user!.Id, member.OrgPeerVisibilityAccessDisabled)));

        return Ok(new { disabled = member.OrgPeerVisibilityAccessDisabled });
    }
}