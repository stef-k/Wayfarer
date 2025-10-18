namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request payload for creating an invitation.
/// </summary>
public class InvitationCreateRequest
{
    public Guid GroupId { get; set; }
    public string? InviteeUserId { get; set; }
    public string? InviteeEmail { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

