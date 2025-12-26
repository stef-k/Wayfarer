namespace Wayfarer.Models.Dtos;

/// <summary>
///     Response DTO for group member listing.
/// </summary>
public class GroupMemberDto
{
    /// <summary>
    ///     Unique identifier of the member user.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    ///     Login username for display or lookup purposes.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Friendly display name shown in UI elements; may be null when unset.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    ///     Group role (Owner, Manager, Member) at the time of listing.
    /// </summary>
    public string GroupRole { get; set; } = string.Empty;

    /// <summary>
    ///     Membership status (Active, Pending, Removed, etc.).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     Deterministic hex color associated with the member (e.g., #RRGGBB).
    ///     Optional; consumers should fallback when null or empty.
    /// </summary>
    public string? ColorHex { get; set; }

    /// <summary>
    ///     Indicates whether the member record represents the requesting user.
    /// </summary>
    public bool IsSelf { get; set; }

    /// <summary>
    ///     Indicates if a member has changed his visibility.
    /// </summary>
    public bool OrgPeerVisibilityAccessDisabled { get; set; }
}