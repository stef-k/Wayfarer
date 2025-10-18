namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request to enable/disable organisation peer visibility on a group.
/// </summary>
public class OrgPeerVisibilityToggleRequest
{
    public bool Enabled { get; set; }
}

