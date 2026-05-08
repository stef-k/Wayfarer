namespace Wayfarer.Models.ViewModels;

/// <summary>
/// Bootstrap values emitted by the MVC shell for the Vue Trip Editor workspace.
/// </summary>
public sealed record TripEditorWorkspaceViewModel
{
    /// <summary>Identifier of the trip to load through the editor API.</summary>
    public Guid TripId { get; init; }

    /// <summary>Display name used before the Vue app finishes loading real state.</summary>
    public required string TripName { get; init; }

    /// <summary>Same-origin URL for the editor read endpoint.</summary>
    public required string EditorEndpointUrl { get; init; }

    /// <summary>Local tile URL template used by the Leaflet adapter.</summary>
    public required string TilesUrl { get; init; }
}
