namespace Wayfarer.Models.Dtos;

/// <summary>Creates a Family or Friends Group.</summary>
public sealed record MobileGroupCreateRequest(string? Name, string? Description, string? GroupType);

/// <summary>Issues an invitation to an exact account selected in a Group-scoped picker.</summary>
public sealed record MobileGroupInviteRequest(string? InviteeUserId);

/// <summary>Accepts the invitation only under the reviewed admission consequences.</summary>
public sealed record MobileGroupAcceptRequest(GroupAdmissionDisclosure? Expected)
{
    /// <summary>Validates all recognized enum strings before authority or resource lookup.</summary>
    internal bool HasValidReview() => Expected is { } review
        && review.GroupType is null or "Family" or "Friends" or "Organization"
        && review.PreferenceSource is "Default" or "Retained"
        && review.VisiblePeerSources is "AllActivePeers" or "SharingActivePeers" or "None"
        && review.HistoryScope == "CurrentMembership";
}

/// <summary>Leaves the current membership only under the reviewed effects.</summary>
public sealed record MobileGroupLeaveRequest(GroupLeaveReview? Expected, string? SuccessorUserId = null)
{
    /// <summary>Validates required values before authority or resource lookup.</summary>
    internal bool HasValidReview() => Expected is { } review
        && !string.IsNullOrWhiteSpace(review.MembershipRef)
        && (SuccessorUserId is null || !string.IsNullOrWhiteSpace(SuccessorUserId))
        && review.Role is "Owner" or "Manager" or "Member"
        && review.Impact is "LeaveOnly" or "TransferOwnership" or "DeleteEmptyGroup" or "DisbandGroup" or "Blocked"
        && review.BlockedReason is null or "organization_no_manager_successor" or "organization_last_manager" or "successor_name_conflict";
}
