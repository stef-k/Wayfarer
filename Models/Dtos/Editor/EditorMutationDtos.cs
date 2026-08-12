namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Complete-draft request for updating trip metadata from the same-origin editor.
/// </summary>
public sealed record EditorTripMetadataUpdateRequest(
    string Name,
    string? NotesHtml,
    bool IsPublic,
    EditorImageUpdateRequest? CoverImage,
    EditorCoordinateDto? Center,
    int? Zoom);

/// <summary>
/// Complete-draft request for replacing all trip-level tags from the same-origin editor.
/// </summary>
public sealed record EditorTripTagsUpdateRequest(IReadOnlyList<string> Tags);

/// <summary>
/// Request for toggling trip share-progress from the same-origin editor.
/// </summary>
public sealed record EditorShareProgressUpdateRequest(bool Enabled);

/// <summary>
/// Cover image update payload containing the raw external URL or a clear instruction.
/// </summary>
public sealed record EditorImageUpdateRequest(string? RawUrl);

/// <summary>
/// Complete-draft request for creating or updating a region from the same-origin editor.
/// </summary>
public sealed record EditorRegionSaveRequest(
    string Name,
    string? NotesHtml,
    EditorImageUpdateRequest? CoverImage,
    EditorCoordinateDto? Center);

/// <summary>
/// Complete desired order for normal editor regions.
/// </summary>
public sealed record EditorRegionOrderRequest(IReadOnlyList<Guid> RegionIds);

/// <summary>
/// Region order data returned by the region order endpoint.
/// </summary>
public sealed record EditorRegionOrderResult(IReadOnlyList<Guid> RegionOrder);

/// <summary>
/// Complete-draft request for creating a place from the same-origin editor.
/// </summary>
public sealed record EditorPlaceCreateRequest(
    string Name,
    string? NotesHtml,
    string? Address,
    EditorCoordinateDto? Location,
    string IconName,
    string MarkerColor,
    bool ReverseGeocode);

/// <summary>
/// Complete-draft request for updating or moving a place from the same-origin editor.
/// </summary>
public sealed record EditorPlaceUpdateRequest(
    Guid RegionId,
    string Name,
    string? NotesHtml,
    string? Address,
    EditorCoordinateDto? Location,
    string IconName,
    string MarkerColor,
    bool ReverseGeocode);

/// <summary>
/// Complete desired place order for one normal editor region.
/// </summary>
public sealed record EditorPlaceOrderRequest(IReadOnlyList<Guid> PlaceIds);

/// <summary>
/// Place order data returned by the place order endpoint.
/// </summary>
public sealed record EditorPlaceOrderResult(Guid RegionId, IReadOnlyList<Guid> PlaceOrder);

/// <summary>
/// Place delete data returned by the place delete endpoint.
/// </summary>
public sealed record EditorPlaceDeleteResult(Guid PlaceId);

/// <summary>
/// Complete-draft request for creating or updating an area from the same-origin editor.
/// </summary>
public sealed record EditorAreaSaveRequest(string Name, string? NotesHtml, string FillHex, NetTopologySuite.Geometries.Polygon Geometry);

/// <summary>
/// Geometry-only request for replacing an area polygon.
/// </summary>
public sealed record EditorAreaGeometryUpdateRequest(NetTopologySuite.Geometries.Polygon Geometry);

/// <summary>
/// Complete desired area order for one normal editor region.
/// </summary>
public sealed record EditorAreaOrderRequest(IReadOnlyList<Guid> AreaIds);

/// <summary>
/// Area order data returned by the area order endpoint.
/// </summary>
public sealed record EditorAreaOrderResult(Guid RegionId, IReadOnlyList<Guid> AreaOrder);

/// <summary>
/// Area delete data returned by the area delete endpoint.
/// </summary>
public sealed record EditorAreaDeleteResult(Guid AreaId);

/// <summary>
/// Complete-draft request for creating or updating a segment from the same-origin editor.
/// </summary>
public sealed record EditorSegmentSaveRequest(
    Guid? FromPlaceId,
    Guid? ToPlaceId,
    IReadOnlyList<Guid> WaypointPlaceIds,
    IReadOnlyList<int?> WaypointRouteVertexIndices,
    string Mode,
    double? EstimatedDistanceKm,
    double? EstimatedDurationMinutes,
    EstimatedDurationSource EstimatedDurationSource,
    string? NotesHtml,
    NetTopologySuite.Geometries.LineString? Route,
    string? AggregateConcurrencyToken);

/// <summary>
/// Complete desired trip-level segment order.
/// </summary>
public sealed record EditorSegmentOrderRequest(IReadOnlyList<Guid> SegmentIds);

/// <summary>
/// Segment order data returned by the segment order endpoint.
/// </summary>
public sealed record EditorSegmentOrderResult(IReadOnlyList<Guid> SegmentOrder);

/// <summary>
/// Segment delete data returned by the segment delete endpoint.
/// </summary>
public sealed record EditorSegmentDeleteResult(Guid SegmentId);

/// <summary>Non-success Segment aggregate state returned for deterministic editor recovery.</summary>
public sealed record EditorSegmentConflictDto(
    string Code,
    string Operation,
    EditorSegmentDto CurrentSegment,
    string Warning,
    DateTimeOffset? ExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore] string? ConfirmationToken);

/// <summary>
/// Standard success envelope returned by editor mutation endpoints.
/// </summary>
public sealed record EditorMutationResult<TData>(
    bool Success,
    TData Data,
    EditorAffectedSlicesDto Affected,
    EditorDeletedIdsDto DeletedIds,
    IReadOnlyList<EditorWarningDto> Warnings);

/// <summary>
/// Concrete editor state slices affected by a mutation.
/// </summary>
public sealed record EditorAffectedSlicesDto(
    EditorTripMetadataDto? Metadata,
    IReadOnlyList<EditorRegionDto> Regions,
    IReadOnlyList<Guid>? RegionOrder,
    IReadOnlyList<EditorPlaceDto> Places,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PlaceOrdersByRegionId,
    IReadOnlyList<EditorAreaDto> Areas,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> AreaOrdersByRegionId,
    IReadOnlyList<EditorSegmentDto> Segments,
    IReadOnlyList<Guid>? SegmentOrder,
    IReadOnlyList<EditorTagDto> Tags,
    IReadOnlyList<string>? TagOrder,
    EditorVisitProgressDto? VisitProgress,
    EditorOptionsDto? Options)
{
    /// <summary>Creates an affected-slices object for a metadata-only mutation.</summary>
    public static EditorAffectedSlicesDto MetadataOnly(EditorTripMetadataDto metadata) =>
        new(
            metadata,
            Array.Empty<EditorRegionDto>(),
            null,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);
}

/// <summary>
/// Entity identifiers deleted by a mutation.
/// </summary>
public sealed record EditorDeletedIdsDto(
    IReadOnlyList<Guid> Regions,
    IReadOnlyList<Guid> Places,
    IReadOnlyList<Guid> Areas,
    IReadOnlyList<Guid> Segments,
    IReadOnlyList<string> Tags)
{
    /// <summary>Creates an empty deleted-id slice for non-delete mutations.</summary>
    public static EditorDeletedIdsDto Empty { get; } =
        new(Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<string>());
}

/// <summary>
/// Non-fatal mutation warning returned to the editor UI.
/// </summary>
public sealed record EditorWarningDto(string Code, string Message, string? EntityType, string? EntityId);

/// <summary>Bounded identifiers returned for one lifecycle dependency category.</summary>
public sealed record EditorLifecycleDependencySampleDto(int Count, IReadOnlyList<Guid> Ids, bool HasMore);

/// <summary>Identifies one waypoint association without exposing Place content.</summary>
public sealed record EditorLifecycleWaypointAssociationDto(Guid SegmentId, Guid PlaceId);

/// <summary>Bounded waypoint-association identities returned for lifecycle confirmation.</summary>
public sealed record EditorLifecycleAssociationSampleDto(
    int Count,
    IReadOnlyList<EditorLifecycleWaypointAssociationDto> Ids,
    bool HasMore);

/// <summary>Opaque server-owned confirmation challenge for destructive lifecycle operations.</summary>
public sealed record EditorLifecycleConflictDto(
    string Code,
    string Operation,
    Guid TargetId,
    EditorLifecycleDependencySampleDto EndpointSegments,
    EditorLifecycleDependencySampleDto WaypointOnlySegments,
    EditorLifecycleAssociationSampleDto WaypointAssociations,
    EditorLifecycleDependencySampleDto DeletedPlaces,
    EditorLifecycleDependencySampleDto DeletedAreas,
    string ConfirmationToken,
    DateTimeOffset ExpiresAt);
