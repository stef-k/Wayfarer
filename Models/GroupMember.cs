using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Wayfarer.Models;

/// <summary>
/// Represents a membership of an ApplicationUser in a Group.
/// </summary>
public class GroupMember
{
    /// <summary>
    /// Primary key.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The group this membership belongs to.
    /// </summary>
    [Required]
    public Guid GroupId { get; set; }

    /// <summary>
    /// The user who is a member of the group.
    /// </summary>
    [Required]
    public required string UserId { get; set; }

    /// <summary>
    /// Member role: Owner, Manager, Member. Stored as string for flexibility.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Role { get; set; } = Roles.Member;

    /// <summary>
    /// Membership status: Active, Left, Removed. Stored as string.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Status { get; set; } = MembershipStatuses.Active;

    /// <summary>
    /// When the user joined the group.
    /// </summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// When the membership ended, if applicable.
    /// </summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>
    /// For Organisation groups, a member can opt out of peer visibility.
    /// </summary>
    public bool OrgPeerVisibilityAccessDisabled { get; set; } = false;

    /// <summary>
    /// Navigation: Group.
    /// </summary>
    [ForeignKey(nameof(GroupId))]
    public virtual Group? Group { get; set; }

    /// <summary>
    /// Navigation: User.
    /// </summary>
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser? User { get; set; }

    /// <summary>
    /// Well-known role constants.
    /// </summary>
    public static class Roles
    {
        public const string Owner = "Owner";
        public const string Manager = "Manager";
        public const string Member = "Member";
    }

    /// <summary>
    /// Well-known membership status constants.
    /// </summary>
    public static class MembershipStatuses
    {
        public const string Active = "Active";
        public const string Left = "Left";
        public const string Removed = "Removed";
    }
}
