using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Wayfarer.Models;

/// <summary>
/// Represents a user-managed group for coordinating members and invitations.
/// </summary>
public class Group
{
    /// <summary>
    /// Primary key.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name of the group (unique per owner).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public required string Name { get; set; }

    /// <summary>
    /// Optional description of the group purpose.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Owning user id (FK to ApplicationUser.Id).
    /// </summary>
    [Required]
    public required string OwnerUserId { get; set; }

    /// <summary>
    /// Indicates whether the group is archived (hidden from active lists).
    /// </summary>
    public bool IsArchived { get; set; } = false;

    /// <summary>
    /// Optional group type. Use "Organisation" for org-specific features.
    /// </summary>
    [MaxLength(50)]
    public string? GroupType { get; set; }

    /// <summary>
    /// When true, members of an Organisation can see each other's locations per policy.
    /// Admin-only toggle. Defaults to false.
    /// </summary>
    public bool OrgPeerVisibilityEnabled { get; set; } = false;

    /// <summary>
    /// Creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last update timestamp (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Navigation: Owner user.
    /// </summary>
    [ForeignKey(nameof(OwnerUserId))]
    public virtual ApplicationUser? Owner { get; set; }

    /// <summary>
    /// Navigation: Members of the group.
    /// </summary>
    public virtual ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();

    /// <summary>
    /// Navigation: Invitations associated to the group.
    /// </summary>
    public virtual ICollection<GroupInvitation> Invitations { get; set; } = new List<GroupInvitation>();
}
