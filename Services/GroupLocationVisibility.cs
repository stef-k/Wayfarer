using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Shared location audience for group queries and protected SSE deliveries.</summary>
public static class GroupLocationVisibility
{
    /// <summary>
    /// Only active members of an unarchived group participate. Personal opt-out hides
    /// the source's locations, never the recipient's access to other willing sources.
    /// Group roles confer no location visibility bypass.
    /// </summary>
    public static bool CanSee(Group group, GroupMember? recipient, GroupMember? source)
    {
        if (group.IsArchived || recipient?.Status != GroupMember.MembershipStatuses.Active
            || source?.Status != GroupMember.MembershipStatuses.Active
            || recipient.GroupId != group.Id || source.GroupId != group.Id) return false;
        if (recipient.UserId == source.UserId) return true;
        if (string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
            return group.OrgPeerVisibilityEnabled && !source.OrgPeerVisibilityAccessDisabled;
        if (string.Equals(group.GroupType, "Friends", StringComparison.OrdinalIgnoreCase))
            return !source.OrgPeerVisibilityAccessDisabled;
        // Legacy untyped groups retain their existing Family-like audience.
        return string.IsNullOrEmpty(group.GroupType)
            || string.Equals(group.GroupType, "Family", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Family has no personal control; Organization preferences are effective only while ON.</summary>
    public static bool CanSetPersonalSharing(Group group) => !group.IsArchived
        && (string.Equals(group.GroupType, "Friends", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase)
                && group.OrgPeerVisibilityEnabled));

    /// <summary>Management requires an active Group role, independently of account or ownership metadata.</summary>
    public static bool CanManage(GroupMember? member) => member?.Status == GroupMember.MembershipStatuses.Active
        && (member.Role == GroupMember.Roles.Owner || member.Role == GroupMember.Roles.Manager);
}
