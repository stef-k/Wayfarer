using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Projects current sharing and the initial consequences compared at admission.</summary>
internal static class GroupSharingPolicy
{
    /// <summary>Canonicalizes known types and preserves unsupported stored policy for readback.</summary>
    internal static string? CanonicalType(string? type) => type?.ToUpperInvariant() switch
    {
        null or "" => null,
        "FAMILY" => "Family",
        "FRIENDS" => "Friends",
        "ORGANIZATION" => "Organization",
        _ => type
    };

    /// <summary>Derives the write eligibility and effective source sharing from the stored preference.</summary>
    internal static GroupPersonalSharing Personal(Group group, GroupMember member)
    {
        var type = CanonicalType(group.GroupType);
        var canSet = GroupLocationVisibility.CanSetPersonalSharing(group);
        var disabled = member.OrgPeerVisibilityAccessDisabled;
        return new(GroupMembershipEpisode.Reference(member), type,
            type == "Organization" ? group.OrgPeerVisibilityEnabled : null, disabled, canSet,
            type is null or "Family" || canSet && !disabled);
    }

    /// <summary>Returns null for unsupported policies; admission eligibility is checked separately.</summary>
    internal static GroupAdmissionDisclosure? Disclosure(Group group, Guid invitationId, GroupMember? member)
    {
        var type = CanonicalType(group.GroupType);
        var disabled = member?.OrgPeerVisibilityAccessDisabled ?? false;
        var sources = type switch
        {
            null or "Family" => "AllActivePeers",
            "Friends" => "SharingActivePeers",
            "Organization" => group.OrgPeerVisibilityEnabled ? "SharingActivePeers" : "None",
            _ => null
        };
        return sources is null ? null : new(invitationId, group.Id, type,
            type == "Organization" ? group.OrgPeerVisibilityEnabled : null,
            member is null ? "Default" : "Retained", disabled,
            sources == "AllActivePeers" || sources == "SharingActivePeers" && !disabled, sources, "CurrentMembership");
    }
}
