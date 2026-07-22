using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Loads place-specific Trip Editor mutation DTOs and affected slices.
/// </summary>
public sealed class TripEditorPlaceMutationReader
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a reader for Trip Editor place mutation responses.
    /// </summary>
    public TripEditorPlaceMutationReader(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Builds the affected slices returned by place mutations.
    /// </summary>
    public async Task<EditorAffectedSlicesDto> BuildAffectedAsync(
        Guid tripId,
        string userId,
        IReadOnlyList<EditorPlaceDto> places,
        IReadOnlyList<Guid> placeOrderRegionIds,
        IReadOnlyList<EditorSegmentDto>? segments,
        IReadOnlyList<Guid>? segmentOrder,
        bool includeVisitProgress,
        CancellationToken cancellationToken)
    {
        var placeOrders = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var regionId in placeOrderRegionIds.Distinct())
        {
            placeOrders[regionId] = await LoadPlaceOrderAsync(regionId, cancellationToken);
        }

        return new EditorAffectedSlicesDto(
            null,
            Array.Empty<EditorRegionDto>(),
            null,
            places,
            placeOrders,
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            segments ?? Array.Empty<EditorSegmentDto>(),
            segmentOrder,
            Array.Empty<EditorTagDto>(),
            null,
            includeVisitProgress ? await LoadVisitProgressAsync(tripId, userId, cancellationToken) : null,
            null);
    }

    /// <summary>
    /// Loads a single place DTO with its visit summary.
    /// </summary>
    public async Task<EditorPlaceDto> LoadPlaceDtoAsync(Guid placeId, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var place = await _dbContext.Places
            .AsNoTracking()
            .SingleAsync(p => p.Id == placeId && p.UserId == userId, cancellationToken);
        var visits = await LoadVisitSummariesAsync(new[] { place }, userId, cancellationToken);
        return EditorTripStateMapper.ToPlace(tripId, place.RegionId, place, visits[place.Id]);
    }

    /// <summary>
    /// Loads ordered place DTOs with their visit summaries.
    /// </summary>
    public async Task<IReadOnlyList<EditorPlaceDto>> LoadPlaceDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var places = await _dbContext.Places
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync(cancellationToken);
        var visits = await LoadVisitSummariesAsync(places, userId, cancellationToken);
        var byId = places.ToDictionary(p => p.Id);
        return ids.Select(id => EditorTripStateMapper.ToPlace(tripId, byId[id].RegionId, byId[id], visits[id])).ToList();
    }

    /// <summary>
    /// Loads segment DTOs in caller-specified order.
    /// </summary>
    public async Task<IReadOnlyList<EditorSegmentDto>> LoadSegmentDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var byId = segments.ToDictionary(s => s.Id);
        return ids.Select(id => EditorTripStateMapper.ToSegment(tripId, byId[id])).ToList();
    }

    /// <summary>
    /// Loads the authoritative place order for one region.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LoadPlaceOrderAsync(Guid regionId, CancellationToken cancellationToken) =>
        await _dbContext.Places
            .AsNoTracking()
            .Where(p => p.RegionId == regionId)
            .OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1)
            .ThenBy(p => p.DisplayOrder)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Loads the authoritative segment order for one trip.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LoadSegmentOrderAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .AsNoTracking()
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto>> LoadVisitSummariesAsync(
        IReadOnlyList<Place> places,
        string userId,
        CancellationToken cancellationToken)
    {
        var placeIds = places.Select(p => p.Id).ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        var visitsByPlaceId = visits.GroupBy(v => v.PlaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());
        return EditorTripStateMapper.ToVisitSummaries(places, visitsByPlaceId);
    }

    private async Task<EditorVisitProgressDto> LoadVisitProgressAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .AsNoTracking()
            .Include(r => r.Places)
            .Where(r => r.TripId == tripId && r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var places = regions.SelectMany(r => r.Places.Select(p => (Region: r, Place: p))).ToList();
        var summaries = await LoadVisitSummariesAsync(places.Select(p => p.Place).ToList(), userId, cancellationToken);
        var visitsByPlaceId = await LoadVisitsByPlaceIdAsync(summaries.Keys, userId, cancellationToken);
        return EditorTripStateMapper.ToVisitProgress(places, summaries, visitsByPlaceId);
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>>> LoadVisitsByPlaceIdAsync(
        IEnumerable<Guid> placeIds,
        string userId,
        CancellationToken cancellationToken)
    {
        var ids = placeIds.ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && ids.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        return visits.GroupBy(v => v.PlaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());
    }
}
