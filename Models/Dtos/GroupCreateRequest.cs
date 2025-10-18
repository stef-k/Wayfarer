namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request payload for creating a group.
/// </summary>
public class GroupCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Type { get; set; } // accepted but not persisted currently
}

