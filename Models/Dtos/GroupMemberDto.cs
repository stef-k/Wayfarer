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
}

