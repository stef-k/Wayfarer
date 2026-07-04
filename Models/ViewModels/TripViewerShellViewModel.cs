namespace Wayfarer.Models.ViewModels;

/// <summary>
/// Bootstrap values emitted by the MVC shell for the read-only Vue Trip Viewer preview.
/// </summary>
public sealed record TripViewerShellViewModel
{
    /// <summary>Identifier of the trip authorized for this shell request.</summary>
    public Guid TripId { get; init; }

    /// <summary>Display name used while the Vue viewer fetches authoritative state.</summary>
    public required string TripName { get; init; }

    /// <summary>Server-derived viewer mode for this preview shell.</summary>
    public required string ViewerMode { get; init; }

    /// <summary>Exact same-origin #335 endpoint the Vue app must fetch.</summary>
    public required string ViewerStateEndpoint { get; init; }

    /// <summary>Public preview URL when a public/open shell URL is available.</summary>
    public string? PublicViewUrl { get; init; }

    /// <summary>Open/fullscreen preview URL for embed mode, excluding <c>embed=true</c>.</summary>
    public string? OpenCanonicalUrl { get; init; }

    /// <summary>Local tile URL template available to the viewer map implementation.</summary>
    public required string TilesUrl { get; init; }

    /// <summary>Tile attribution text from application settings.</summary>
    public required string TileAttribution { get; init; }

    /// <summary>True when the shell should use the chrome-free embed layout.</summary>
    public bool IsEmbed => ViewerMode == "embed";
}
