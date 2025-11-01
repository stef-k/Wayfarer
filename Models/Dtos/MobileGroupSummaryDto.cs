namespace Wayfarer.Models.Dtos;

/// <summary>
/// Represents a group summary tailored for mobile clients.
/// </summary>
public class MobileGroupSummaryDto
{
    /// <summary>
    /// Unique identifier of the group.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-friendly name presented to the mobile user.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Optional description that may appear in detail views.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Group type label (e.g., Friends, Organization) used for policy decisions.
    /// </summary>
    public string? GroupType { get; set; }

    /// <summary>
    /// Indicates whether organisation peer visibility is enabled at group level.
    /// </summary>
    public bool OrgPeerVisibilityEnabled { get; set; }

    /// <summary>
    /// Number of active members currently in the group.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// True when the current user owns the group.
    /// </summary>
    public bool IsOwner { get; set; }

    /// <summary>
    /// True when the current user is a manager/co-admin.
    /// </summary>
    public bool IsManager { get; set; }

    /// <summary>
    /// True when the current user is an active member (non-admin role).
    /// </summary>
    public bool IsMember { get; set; }

    /// <summary>
    /// For organisation groups, indicates whether the user can view peers.
    /// </summary>
    public bool HasOrgPeerVisibilityAccess { get; set; }
}
