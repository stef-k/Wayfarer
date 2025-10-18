namespace Wayfarer.Models.Dtos;

/// <summary>
/// Request for group latest locations.
/// </summary>
public class GroupLocationsLatestRequest
{
    public List<string>? IncludeUserIds { get; set; }
}

