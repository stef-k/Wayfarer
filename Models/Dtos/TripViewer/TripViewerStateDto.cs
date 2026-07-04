using System.Text.Json;

namespace Wayfarer.Models.Dtos.TripViewer;

/// <summary>
/// Read-only state contract returned by the preview Trip Viewer endpoints.
/// </summary>
public sealed record TripViewerStateDto
{
    /// <summary>Server-derived viewer mode: private, public, or embed.</summary>
    public required string ViewerMode { get; init; }

    /// <summary>Top-level trip metadata for read-only display.</summary>
    public required TripViewerTripDto Trip { get; init; }

    /// <summary>Regions keyed by region identifier.</summary>
    public required IReadOnlyDictionary<Guid, TripViewerRegionDto> RegionsById { get; init; }

    /// <summary>Authoritative region display order.</summary>
    public required IReadOnlyList<Guid> RegionOrder { get; init; }

    /// <summary>Places keyed by place identifier.</summary>
    public required IReadOnlyDictionary<Guid, TripViewerPlaceDto> PlacesById { get; init; }

    /// <summary>Place order keyed by parent region identifier.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PlaceOrderByRegionId { get; init; }

    /// <summary>Areas keyed by area identifier.</summary>
    public required IReadOnlyDictionary<Guid, TripViewerAreaDto> AreasById { get; init; }

    /// <summary>Area order keyed by parent region identifier.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> AreaOrderByRegionId { get; init; }

    /// <summary>Segments keyed by segment identifier.</summary>
    public required IReadOnlyDictionary<Guid, TripViewerSegmentDto> SegmentsById { get; init; }

    /// <summary>Authoritative segment display order.</summary>
    public required IReadOnlyList<Guid> SegmentOrder { get; init; }

    /// <summary>Tags keyed by slug.</summary>
    public required IReadOnlyDictionary<string, TripViewerTagDto> TagsBySlug { get; init; }

    /// <summary>Authoritative tag order by slug.</summary>
    public required IReadOnlyList<string> TagOrder { get; init; }

    /// <summary>Server-redacted visit progress state.</summary>
    public required TripViewerVisitProgressDto VisitProgress { get; init; }

    /// <summary>Server-derived permissions for the returned state.</summary>
    public required TripViewerPermissionsDto Permissions { get; init; }

    /// <summary>Server-generated action flags and URLs.</summary>
    public required TripViewerActionsDto Actions { get; init; }

    /// <summary>Map state and query compatibility contract.</summary>
    public required TripViewerMapDto Map { get; init; }
}

/// <summary>Trip metadata included in viewer state.</summary>
public sealed record TripViewerTripDto(
    Guid Id,
    string Name,
    TripViewerNotesDto Notes,
    bool IsPublic,
    bool ShareProgressEnabled,
    string? OwnerDisplayName,
    TripViewerCoverImageDto? CoverImage,
    TripViewerCoordinateDto? Center,
    int? Zoom,
    DateTime UpdatedAt,
    string PrivateUrl,
    string PublicUrl,
    string PublicEmbedUrl);

/// <summary>Display-safe notes payload from the #334 contract.</summary>
public sealed record TripViewerNotesDto(
    string DisplayHtml,
    string PlainText,
    bool HasRenderableContent,
    bool HasTextContent,
    bool HasMediaContent);

/// <summary>Cover image display and copy URLs.</summary>
public sealed record TripViewerCoverImageDto(string DisplayUrl, string? CopyUrl, string? RawUrl);

/// <summary>Named coordinate for non-GeoJSON points.</summary>
public sealed record TripViewerCoordinateDto(double Latitude, double Longitude);

/// <summary>Region DTO with notes and child order references.</summary>
public sealed record TripViewerRegionDto(
    Guid Id,
    Guid TripId,
    string Name,
    TripViewerNotesDto Notes,
    TripViewerCoverImageDto? CoverImage,
    TripViewerCoordinateDto? Center,
    int DisplayOrder,
    IReadOnlyList<Guid> PlaceIds,
    IReadOnlyList<Guid> AreaIds);

/// <summary>Place DTO with display metadata and redacted visit summary.</summary>
public sealed record TripViewerPlaceDto(
    Guid Id,
    Guid TripId,
    Guid RegionId,
    string Name,
    TripViewerNotesDto Notes,
    string Address,
    TripViewerCoordinateDto? Location,
    string IconName,
    string MarkerColor,
    int DisplayOrder,
    TripViewerPlaceVisitSummaryDto VisitSummary);

/// <summary>Area DTO with GeoJSON geometry.</summary>
public sealed record TripViewerAreaDto(
    Guid Id,
    Guid TripId,
    Guid RegionId,
    string Name,
    TripViewerNotesDto Notes,
    string FillHex,
    JsonElement? Geometry,
    int DisplayOrder);

/// <summary>Segment DTO with GeoJSON route and coordinate fallbacks.</summary>
public sealed record TripViewerSegmentDto(
    Guid Id,
    Guid TripId,
    Guid? FromPlaceId,
    Guid? ToPlaceId,
    string Mode,
    double? EstimatedDistanceKm,
    double? EstimatedDurationMinutes,
    TripViewerNotesDto Notes,
    JsonElement? Route,
    TripViewerCoordinateDto? FallbackStart,
    TripViewerCoordinateDto? FallbackEnd,
    int DisplayOrder);

/// <summary>Tag DTO used by viewer state.</summary>
public sealed record TripViewerTagDto(Guid Id, string Name, string Slug);

/// <summary>Redacted visit progress state.</summary>
public sealed record TripViewerVisitProgressDto(
    bool CanDisplayProgress,
    bool CanDisplayCounts,
    bool CanDisplayHistory,
    int TotalPlaces,
    int VisitedPlaces,
    double PercentVisited,
    IReadOnlyDictionary<Guid, TripViewerPlaceVisitSummaryDto> PlaceSummariesByPlaceId,
    IReadOnlyList<TripViewerVisitHistoryRowDto> HistoryRows);

/// <summary>Visit summary for one place.</summary>
public sealed record TripViewerPlaceVisitSummaryDto(
    Guid PlaceId,
    int VisitCount,
    bool IsVisited,
    DateTime? FirstVisitAt,
    DateTime? LastVisitAt);

/// <summary>Visit history row returned only when history is allowed.</summary>
public sealed record TripViewerVisitHistoryRowDto(
    Guid VisitId,
    Guid PlaceId,
    Guid RegionId,
    DateTime StartedAt,
    DateTime? EndedAt,
    int? DurationMinutes);

/// <summary>Server-derived viewer permissions.</summary>
public sealed record TripViewerPermissionsDto(
    bool CanViewPrivateState,
    bool CanViewPublicState,
    bool CanViewEmbedState,
    bool IsOwner,
    bool CanReadNotes,
    bool CanReadVisitCounts,
    bool CanReadVisitHistory,
    bool CanToggleShareProgress,
    bool CanUseReadableMode,
    bool CanPrint);

/// <summary>All server-generated viewer actions.</summary>
public sealed class TripViewerActionsDto
{
    /// <summary>Owner edit navigation action.</summary>
    public required TripViewerActionDto Edit { get; init; }

    /// <summary>Clone or login-to-clone action.</summary>
    public required TripViewerActionDto Clone { get; init; }

    /// <summary>Wayfarer KML export action.</summary>
    public required TripViewerActionDto ExportWayfarerKml { get; init; }

    /// <summary>Google My Maps KML export action.</summary>
    public required TripViewerActionDto ExportGoogleMyMapsKml { get; init; }

    /// <summary>PDF export action.</summary>
    public required TripViewerActionDto ExportPdf { get; init; }

    /// <summary>Share action.</summary>
    public required TripViewerActionDto Share { get; init; }

    /// <summary>Copy public URL action.</summary>
    public required TripViewerActionDto CopyPublicUrl { get; init; }

    /// <summary>Copy cover URL action.</summary>
    public required TripViewerActionDto CopyCoverUrl { get; init; }

    /// <summary>Copy map snapshot URL action.</summary>
    public required TripViewerActionDto CopyMapSnapshotUrl { get; init; }

    /// <summary>Fullscreen action used by embed mode.</summary>
    public required TripViewerActionDto Fullscreen { get; init; }

    /// <summary>Open canonical public viewer action used by embed mode.</summary>
    public required TripViewerActionDto OpenCanonical { get; init; }

    /// <summary>Readable mode action.</summary>
    public required TripViewerActionDto Readable { get; init; }

    /// <summary>Print action.</summary>
    public required TripViewerActionDto Print { get; init; }
}

/// <summary>Single viewer action flag and optional URL details.</summary>
public sealed record TripViewerActionDto(
    bool Allowed,
    string? Url = null,
    string? Method = null,
    bool RequiresAuthentication = false);

/// <summary>Map initial state and query compatibility metadata.</summary>
public sealed record TripViewerMapDto(
    TripViewerMapInitialViewDto InitialView,
    IReadOnlyList<string> AcceptedQueryParameters,
    IReadOnlyList<string> EmittedQueryParameters,
    string TileUrlTemplate,
    string TileAttribution);

/// <summary>Resolved initial map position plus canonical query output.</summary>
public sealed record TripViewerMapInitialViewDto(
    double Latitude,
    double Longitude,
    int Zoom,
    string Source,
    string CanonicalQuery);
