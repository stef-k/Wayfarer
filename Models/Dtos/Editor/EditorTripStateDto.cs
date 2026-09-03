using System.Text.Json;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Normalized read model returned by the first Trip Editor workspace endpoint.
/// </summary>
public sealed record EditorTripStateDto
{
    /// <summary>Identifier of the trip loaded into the editor workspace.</summary>
    public Guid TripId { get; init; }

    /// <summary>Top-level trip metadata used by the read-only workspace shell.</summary>
    public required EditorTripMetadataDto Metadata { get; init; }

    /// <summary>Regions keyed by region identifier.</summary>
    public required IReadOnlyDictionary<Guid, EditorRegionDto> RegionsById { get; init; }

    /// <summary>Authoritative region display order.</summary>
    public required IReadOnlyList<Guid> RegionOrder { get; init; }

    /// <summary>Places keyed by place identifier.</summary>
    public required IReadOnlyDictionary<Guid, EditorPlaceDto> PlacesById { get; init; }

    /// <summary>Place display order keyed by parent region identifier.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PlaceOrderByRegionId { get; init; }

    /// <summary>Areas keyed by area identifier.</summary>
    public required IReadOnlyDictionary<Guid, EditorAreaDto> AreasById { get; init; }

    /// <summary>Area display order keyed by parent region identifier.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> AreaOrderByRegionId { get; init; }

    /// <summary>Segments keyed by segment identifier.</summary>
    public required IReadOnlyDictionary<Guid, EditorSegmentDto> SegmentsById { get; init; }

    /// <summary>Authoritative segment display order.</summary>
    public required IReadOnlyList<Guid> SegmentOrder { get; init; }

    /// <summary>Tags keyed by slug.</summary>
    public required IReadOnlyDictionary<string, EditorTagDto> TagsBySlug { get; init; }

    /// <summary>Authoritative tag display order by slug.</summary>
    public required IReadOnlyList<string> TagOrder { get; init; }

    /// <summary>Read-only visit progress summary for this trip.</summary>
    public required EditorVisitProgressDto VisitProgress { get; init; }

    /// <summary>Deterministic editor option sections for current and future UI slices.</summary>
    public required EditorOptionsDto Options { get; init; }

    /// <summary>Permissions granted to the current user for this editor state.</summary>
    public required EditorPermissionsDto Permissions { get; init; }
}

/// <summary>Trip metadata included in the editor read model.</summary>
public sealed record EditorTripMetadataDto(
    Guid Id,
    string Name,
    string NotesHtml,
    bool IsPublic,
    bool ShareProgressEnabled,
    EditorCoordinateDto? Center,
    int? Zoom,
    EditorImageReferenceDto? CoverImage,
    DateTime UpdatedAt,
    string? PublicUrl,
    string? ProgressPublicUrl);

/// <summary>Region DTO with explicit shadow/capability flags.</summary>
public sealed record EditorRegionDto(
    Guid Id,
    Guid TripId,
    string Name,
    string NotesHtml,
    EditorImageReferenceDto? CoverImage,
    EditorCoordinateDto? Center,
    int DisplayOrder,
    bool IsShadow,
    EditorEntityCapabilitiesDto Capabilities);

/// <summary>Place DTO with explicit parent region and visit summary.</summary>
public sealed record EditorPlaceDto(
    Guid Id,
    Guid TripId,
    Guid RegionId,
    string Name,
    string NotesHtml,
    string Address,
    string? ResolvedFeatureName,
    string? ResolvedFeatureType,
    EditorCoordinateDto? Location,
    string IconName,
    string MarkerColor,
    int DisplayOrder,
    EditorPlaceVisitSummaryDto VisitSummary,
    EditorEntityCapabilitiesDto Capabilities);

/// <summary>Area DTO with GeoJSON object geometry.</summary>
public sealed record EditorAreaDto(
    Guid Id,
    Guid TripId,
    Guid RegionId,
    string Name,
    string NotesHtml,
    string FillHex,
    JsonElement Geometry,
    int DisplayOrder,
    EditorEntityCapabilitiesDto Capabilities);

/// <summary>Segment DTO with GeoJSON object route geometry.</summary>
public sealed record EditorSegmentDto(
    Guid Id,
    Guid TripId,
    Guid? FromPlaceId,
    Guid? ToPlaceId,
    IReadOnlyList<Guid> WaypointPlaceIds,
    IReadOnlyList<int?> WaypointRouteVertexIndices,
    string Mode,
    Guid? TransportProfileId,
    bool HasCustomRoute,
    double? EstimatedDistanceKm,
    double? EstimatedDurationMinutes,
    string EstimatedDurationSource,
    string NotesHtml,
    JsonElement? Route,
    JsonElement? EffectiveRoute,
    string AggregateConcurrencyToken,
    int DisplayOrder,
    EditorEntityCapabilitiesDto Capabilities,
    EditorExternalRoutingCapabilityDto? ExternalRouting = null);

/// <summary>Safe per-Segment external-routing capability without endpoint or configuration details.</summary>
public sealed record EditorExternalRoutingCapabilityDto(
    bool Available, string? UnavailableReason, string? ProviderDisplayName, string? MappedProfileLabel,
    string? Disclosure, string? Attribution, IReadOnlyList<ProviderDirectionsMode>? ProviderModes = null)
{
    /// <summary>Gets the current closed provider-native modes.</summary>
    public IReadOnlyList<ProviderDirectionsMode> Modes => ProviderModes ?? [];
}

/// <summary>Tag DTO used by the editor state.</summary>
public sealed record EditorTagDto(Guid Id, string Name, string Slug);

/// <summary>Read-only visit progress summary.</summary>
public sealed record EditorVisitProgressDto(
    int TotalPlaces,
    int VisitedPlaces,
    double PercentVisited,
    IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> PlaceSummariesByPlaceId,
    IReadOnlyList<EditorVisitHistoryRowDto> HistoryRows);

/// <summary>Read-only visit summary for a single place.</summary>
public sealed record EditorPlaceVisitSummaryDto(
    Guid PlaceId,
    int VisitCount,
    bool IsVisited,
    DateTime? FirstVisitAt,
    DateTime? LastVisitAt);

/// <summary>Read-only visit history row for the editor visit progress state.</summary>
public sealed record EditorVisitHistoryRowDto(
    Guid VisitId,
    Guid PlaceId,
    Guid RegionId,
    DateTime StartedAt,
    DateTime? EndedAt,
    int? DurationMinutes);

/// <summary>Deterministic options object for the read-only spike and future editors.</summary>
public sealed record EditorOptionsDto(
    IReadOnlyList<string> IconNames,
    IReadOnlyList<string> MarkerColorClasses,
    IReadOnlyList<string> GlyphColorClasses,
    IReadOnlyList<EditorTransportModeDto> TransportModes,
    EditorAreaDefaultsDto AreaDefaults,
    EditorTagOptionsDto Tag,
    EditorLimitsDto Limits);

/// <summary>Transport mode option used by future segment forms.</summary>
public sealed record EditorTransportModeDto(string Value, string Label, double? SpeedKmh);

/// <summary>Default values for future area creation.</summary>
public sealed record EditorAreaDefaultsDto(string Name, string FillHex);

/// <summary>Tag behavior options for future tag editing.</summary>
public sealed record EditorTagOptionsDto(int MaxTags, int SuggestionTake, string AllowedPatternDescription);

/// <summary>Small numeric limits used by future editor workflows.</summary>
public sealed record EditorLimitsDto(int NominatimSearchLimit, int SidebarSearchMinCharacters);

/// <summary>Top-level editor permissions for the current user.</summary>
public sealed record EditorPermissionsDto(
    bool CanEditTrip,
    bool CanEditMetadata,
    bool CanEditRegions,
    bool CanEditPlaces,
    bool CanEditAreas,
    bool CanEditSegments,
    bool CanEditTags,
    bool CanToggleShareProgress,
    bool CanReadVisitProgress);

/// <summary>Per-entity capabilities used instead of frontend name inference.</summary>
public sealed record EditorEntityCapabilitiesDto(
    bool CanEdit,
    bool CanRename,
    bool CanDelete,
    bool CanReorder,
    bool CanMove,
    bool CanAddChildren,
    bool CanTargetForSearchAdd);

/// <summary>Named coordinate for non-GeoJSON points.</summary>
public sealed record EditorCoordinateDto(double Latitude, double Longitude);

/// <summary>Raw and proxied image URLs for editor display.</summary>
public sealed record EditorImageReferenceDto(string RawUrl, string ProxiedUrl);
