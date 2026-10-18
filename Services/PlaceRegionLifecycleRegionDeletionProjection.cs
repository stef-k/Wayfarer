using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Projects final authoritative slices before commit; no kept route is fabricated or rewritten.</summary>
    private async Task<EditorMutationResult<RegionDeletionResultDto>> ProjectRegionDeletionAsync(
        RegionDeletionSnapshot s, string userId, RegionDeletionRequestDto request, Place[] orderedPlaces,
        Area[] orderedAreas, Region[] regions, IReadOnlySet<Guid> deletedPlaces, Guid[] deletedAreas,
        IReadOnlySet<Guid> deletedSegments, Guid[] surviving, EditorLifecycleWaypointAssociationDto[] detached,
        CancellationToken token)
    {
        var reader = new TripEditorPlaceMutationReader(_dbContext, _confirmation.RegionDeletionAggregateTokens);
        var places = await reader.LoadPlaceDtosAsync(orderedPlaces.Select(p => p.Id).ToArray(), s.Trip.Id, userId, token);
        var segments = await reader.LoadSegmentDtosAsync(surviving, s.Trip.Id, userId, token);
        var baseSlices = await reader.BuildAffectedAsync(s.Trip.Id, userId, places, [], segments, null, true, token);
        IReadOnlyList<Guid>? segmentOrder = deletedSegments.Count > 0
            ? await _dbContext.Segments.AsNoTracking().Where(x => x.TripId == s.Trip.Id && x.UserId == userId)
                .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token) : null;
        var affected = baseSlices with
        {
            Metadata = EditorTripStateMapper.ToMetadata(s.Trip, null, null),
            Regions = regions.Select(EditorTripStateMapper.ToRegion).ToArray(),
            RegionOrder = regions.Select(r => r.Id).ToArray(),
            PlaceOrdersByRegionId = orderedPlaces.Length > 0
                ? new Dictionary<Guid, IReadOnlyList<Guid>> { [s.Destination.RegionId!.Value] = orderedPlaces.Select(p => p.Id).ToArray() } : new(),
            Areas = orderedAreas.Select(a => EditorTripStateMapper.ToArea(s.Trip.Id, a.RegionId, a)).ToArray(),
            AreaOrdersByRegionId = orderedAreas.Length > 0
                ? new Dictionary<Guid, IReadOnlyList<Guid>> { [s.Destination.RegionId!.Value] = orderedAreas.Select(a => a.Id).ToArray() } : new(),
            SegmentOrder = segmentOrder
        };
        var data = new RegionDeletionResultDto(s.Trip.Id, s.Source.Id, request.Places, request.Areas, s.Destination.RegionId,
            request.Places == "keep" ? s.Places.Select(p => p.Id).Order().ToArray() : [],
            request.Areas == "keep" ? s.Areas.Select(a => a.Id).Order().ToArray() : [], surviving.Order().ToArray(), detached);
        return new(true, data, affected,
            new([s.Source.Id], deletedPlaces.Order().ToArray(), deletedAreas.Order().ToArray(), deletedSegments.Order().ToArray(), []), []);
    }
}
