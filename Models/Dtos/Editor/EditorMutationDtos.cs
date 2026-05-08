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
