using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>The sharing consequences an invitation recipient reviews before admission.</summary>
public sealed record GroupAdmissionDisclosure(
    [property: JsonRequired] Guid InvitationId,
    [property: JsonRequired] Guid GroupId,
    [property: JsonRequired] string? GroupType,
    [property: JsonRequired] bool? OrgPeerVisibilityEnabled,
    [property: JsonRequired] string PreferenceSource,
    [property: JsonRequired] bool Disabled,
    [property: JsonRequired] bool SharesWithPeers,
    [property: JsonRequired] string VisiblePeerSources,
    [property: JsonRequired] string HistoryScope);

/// <summary>The membership episode and effects presented for explicit Leave confirmation.</summary>
public sealed record GroupLeaveReview(
    [property: JsonRequired] Guid GroupId,
    [property: JsonRequired] string MembershipRef,
    [property: JsonRequired] string Role,
    [property: JsonRequired] bool IsActualOwner,
    [property: JsonRequired] string Impact,
    [property: JsonRequired] string? SuccessorUserId,
    [property: JsonRequired] string? BlockedReason);

/// <summary>Leave review with contextual labels excluded from the comparison.</summary>
public sealed record GroupLeavePreview(string GroupName, GroupLeaveReview Review, string? SuccessorDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GroupLeaveSuccessor>? SuccessorCandidates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SelectedSuccessorUserId = null);

/// <summary>An active account to whom the owner can choose to transfer ownership before leaving.</summary>
public sealed record GroupLeaveSuccessor(string UserId, string? DisplayName);

/// <summary>Committed effects of a confirmed departure.</summary>
public sealed record GroupLeaveResult(Guid GroupId, string Impact, string? SuccessorUserId);

/// <summary>Current stored and effective personal sharing for a membership episode.</summary>
public sealed record GroupPersonalSharing(string MembershipRef, string? GroupType, bool? OrgPeerVisibilityEnabled,
    bool Disabled, bool CanSet, bool SharesWithPeers);
