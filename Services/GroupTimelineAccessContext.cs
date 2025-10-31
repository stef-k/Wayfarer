using System.Collections.Generic;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// Represents caller-specific access information for group timeline queries.
/// </summary>
public sealed class GroupTimelineAccessContext
{
    internal GroupTimelineAccessContext(
        Group group,
        GroupMember? callerMembership,
        IReadOnlyList<GroupMember> activeMembers,
        IReadOnlyCollection<string> allowedUserIds,
        bool isFriends,
        int locationTimeThresholdMinutes)
    {
        Group = group;
        CallerMembership = callerMembership;
        ActiveMembers = activeMembers;
        AllowedUserIds = allowedUserIds;
        IsFriends = isFriends;
        LocationTimeThresholdMinutes = locationTimeThresholdMinutes;
    }

    public Group Group { get; }
    public GroupMember? CallerMembership { get; }
    public IReadOnlyList<GroupMember> ActiveMembers { get; }
    public IReadOnlyCollection<string> AllowedUserIds { get; }
    public bool IsMember => CallerMembership != null;
    public bool IsFriends { get; }
    public int LocationTimeThresholdMinutes { get; }
}
