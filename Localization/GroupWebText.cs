using Wayfarer.Models;

namespace Wayfarer.Localization;

/// <summary>Group presentation labels for canonical values and MVC action outcomes.</summary>
public static class GroupWebText
{
    /// <summary>Translates known group types while retaining any other stored label.</summary>
    public static string Type(WebMessages text, string? type) => type?.ToLowerInvariant() switch
    {
        "family" => text["Groups.TypeFamily"],
        "friends" => text["Groups.TypeFriends"],
        "organization" => text["Groups.TypeOrganization"],
        _ => type ?? string.Empty
    };

    /// <summary>Translates the membership role without changing the role used for authorization.</summary>
    public static string Role(WebMessages text, string role) => role switch
    {
        GroupMember.Roles.Owner => text["Groups.RoleOwner"],
        GroupMember.Roles.Manager => text["Groups.RoleManager"],
        GroupMember.Roles.Member => text["Groups.RoleMember"],
        _ => role
    };

    /// <summary>Maps a bounded legacy outcome to the same message identity used by Web AJAX feedback.</summary>
    public static string ErrorKey(string message, string fallback) => message switch
    {
        "Group with the same name already exists for owner" => "Groups.DuplicateName",
        "A pending invitation already exists for this user in the specified group" => "Groups.DuplicateInvite",
        "User already an active member" => "Groups.AlreadyMember",
        "InviteeUserId required" => "Groups.SelectInvitee",
        "InviteeUserId must identify an existing user" => "Groups.AccountUnavailable",
        "Cannot remove the last manager from an Organization group." => "Groups.LastManagerRemove",
        "Cannot remove owner from Organization group without an eligible Manager successor. Promote a member to Manager first or delete the group."
            => "Groups.OwnerRemoveSuccession",
        _ => fallback
    };
}
