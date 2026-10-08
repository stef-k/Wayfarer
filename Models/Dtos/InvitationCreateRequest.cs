namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request payload for creating an invitation.
/// </summary>
public class InvitationCreateRequest
{
    /// <summary>Destination Group ID.</summary>
    public Guid GroupId { get; set; }
    /// <summary>Required ID of the registered account selected by username/display name.</summary>
    public string? InviteeUserId { get; set; }
    /// <summary>Optional invitation expiration timestamp.</summary>
    public DateTime? ExpiresAt { get; set; }
}

