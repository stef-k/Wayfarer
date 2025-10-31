namespace Wayfarer.Models.Dtos;

/// <summary>
/// Response DTO for group member listing.
/// </summary>
public class GroupMemberDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string GroupRole { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Deterministic hex color associated with the member (e.g., #RRGGBB).
    /// Optional; consumers should fallback when null or empty.
    /// </summary>
    public string? ColorHex { get; set; }

    /// <summary>
    /// Indicates whether the member record represents the requesting user.
    /// </summary>
    public bool IsSelf { get; set; }

    /// <summary>
    /// SSE channel identifier for per-user location updates, if available.
    /// </summary>
    public string? SseChannel { get; set; }
}
