namespace Wayfarer.Models.Dtos;

/// <summary>
/// Represents a group summary tailored for mobile clients.
/// </summary>
public class MobileGroupSummaryDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? GroupType { get; set; }
    public bool OrgPeerVisibilityEnabled { get; set; }
    public int MemberCount { get; set; }
    public bool IsOwner { get; set; }
    public bool IsManager { get; set; }
    public bool IsMember { get; set; }
    public bool HasOrgPeerVisibilityAccess { get; set; }
}
