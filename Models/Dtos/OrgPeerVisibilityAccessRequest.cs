namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request to opt out/in of peer visibility for a member in an Organization group.
/// </summary>
public class OrgPeerVisibilityAccessRequest
{
    public bool Disabled { get; set; }
}

