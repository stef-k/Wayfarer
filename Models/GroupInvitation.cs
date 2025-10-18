using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Wayfarer.Models;

/// <summary>
/// Represents an invitation for a user (or email) to join a group.
/// </summary>
public class GroupInvitation
{
    /// <summary>
    /// Primary key.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Target group id.
    /// </summary>
    [Required]
    public Guid GroupId { get; set; }

    /// <summary>
    /// Inviting user id.
    /// </summary>
    [Required]
    public required string InviterUserId { get; set; }

    /// <summary>
    /// Invitee user id if known at invite time.
    /// </summary>
    public string? InviteeUserId { get; set; }

    /// <summary>
    /// Optional email of invitee when user account is not yet known.
    /// </summary>
    [MaxLength(256)]
    public string? InviteeEmail { get; set; }

    /// <summary>
    /// Unique token for accepting the invitation.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public required string Token { get; set; }

    /// <summary>
    /// Invitation status: Pending, Accepted, Declined, Revoked, Expired.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Status { get; set; } = InvitationStatuses.Pending;

    /// <summary>
    /// Optional expiry time.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Response timestamp (UTC) when invite was accepted/declined.
    /// </summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>
    /// Navigation: Group.
    /// </summary>
    [ForeignKey(nameof(GroupId))]
    public virtual Group? Group { get; set; }

    /// <summary>
    /// Navigation: Inviter user.
    /// </summary>
    [ForeignKey(nameof(InviterUserId))]
    public virtual ApplicationUser? Inviter { get; set; }

    /// <summary>
    /// Navigation: Invitee user.
    /// </summary>
    [ForeignKey(nameof(InviteeUserId))]
    public virtual ApplicationUser? Invitee { get; set; }

    /// <summary>
    /// Well-known invitation status constants.
    /// </summary>
    public static class InvitationStatuses
    {
        public const string Pending = "Pending";
        public const string Accepted = "Accepted";
        public const string Declined = "Declined";
        public const string Revoked = "Revoked";
        public const string Expired = "Expired";
    }
}

