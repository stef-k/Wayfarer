using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Reads the actual destructive footprint without hiding malformed foreign dependencies.</summary>
    private async Task<PlaceDeletionSnapshot> ReadPlaceDeletionAsync(Guid tripId, Guid placeId, string userId, CancellationToken token)
    {
        var trip = await _dbContext.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, token);
        var place = await _dbContext.Places.AsNoTracking().Include(p => p.Region)
            .SingleOrDefaultAsync(p => p.Id == placeId && p.UserId == userId && p.Region.UserId == userId && p.Region.TripId == tripId, token);
        if (trip == null || place == null) throw PlaceStructureUnavailable();
        var siblings = await _dbContext.Places.AsNoTracking().Where(p => p.RegionId == place.RegionId).OrderBy(p => p.Id).ToArrayAsync(token);
        var segments = await _dbContext.Segments.AsNoTracking().Include(s => s.Waypoints)
            .Where(s => s.FromPlaceId == placeId || s.ToPlaceId == placeId || s.Waypoints.Any(w => w.PlaceId == placeId))
            .OrderBy(s => s.Id).ToArrayAsync(token);
        var endpointIds = segments.Where(s => s.FromPlaceId == placeId || s.ToPlaceId == placeId).Select(s => s.Id).ToArray();
        var orders = endpointIds.Length > 0 ? await _dbContext.Segments.AsNoTracking().Where(s => s.TripId == tripId)
            .OrderBy(s => s.Id).ToArrayAsync(token) : [];
        var anchorIds = segments.SelectMany(s => s.Waypoints.Select(w => w.PlaceId)
            .Concat(new[] { s.FromPlaceId, s.ToPlaceId }.OfType<Guid>())).Distinct().ToArray();
        var anchors = await _dbContext.Places.AsNoTracking().Include(p => p.Region)
            .Where(p => anchorIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        var profileIds = segments.Select(s => s.TransportProfileId).OfType<Guid>().Distinct().ToArray();
        var profiles = await _dbContext.Set<TransportProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        if (siblings.Any(p => p.UserId != userId) || segments.Concat(orders).Any(s => s.TripId != tripId || s.UserId != userId)
            || anchors.Length != anchorIds.Length || profiles.Length != profileIds.Length
            || anchors.Any(p => p.UserId != userId || p.Region.UserId != userId || p.Region.TripId != tripId)) throw PlaceStructureMalformed();
        var visits = await _dbContext.PlaceVisitEvents.AsNoTracking().Where(v => v.PlaceId == placeId).OrderBy(v => v.Id).Select(v => v.Id).ToArrayAsync(token);
        var candidates = await _dbContext.PlaceVisitCandidates.AsNoTracking().Where(v => v.PlaceId == placeId).OrderBy(v => v.Id).Select(v => v.Id).ToArrayAsync(token);
        return new(trip, place, siblings, segments, orders, anchors, profiles, endpointIds, visits, candidates);
    }

    /// <summary>Retains full target/parent/route contents; sibling proofs cover precisely the ordering writes.</summary>
    private string PlaceDeletionFingerprint(PlaceDeletionSnapshot s) => PlaceStructuralRequest.Hash(new
    {
        Target = DeletionScalars(s.Place), Parent = DeletionScalars(s.Place.Region),
        Siblings = s.Siblings.Select(p => new { p.Id, p.UserId, p.RegionId, p.DisplayOrder }),
        Segments = s.Segments.Select(x => new { Values = DeletionScalars(x),
            Waypoints = x.Waypoints.OrderBy(w => w.Position).ThenBy(w => w.PlaceId).Select(DeletionScalars) }),
        SegmentOrders = s.SegmentOrders.Select(x => new { x.Id, x.UserId, x.TripId, x.DisplayOrder }),
        Anchors = s.Anchors.Select(p => new { p.Id, p.UserId, p.RegionId, p.Region.TripId, RegionOwner = p.Region.UserId, Location = FingerprintValue(p.Location) }),
        Profiles = s.Profiles.Select(DeletionScalars), s.Visits, s.Candidates
    });

    /// <summary>Uses the mapped material scalar contract shared with conservative Region consent.</summary>
    private object DeletionScalars(object entity) => _dbContext.Model.FindEntityType(entity.GetType())!.GetProperties()
        .Where(p => !p.IsShadowProperty()).OrderBy(p => p.Name, StringComparer.Ordinal)
        .Select(p => new { p.Name, Value = FingerprintValue(p.PropertyInfo!.GetValue(entity)) }).ToArray();

    /// <summary>Publishes complete public effect/order identities so acknowledgements can be checked before adoption.</summary>
    private PlaceDeletionPreviewDto BuildPlaceDeletionPreview(PlaceDeletionSnapshot s, string userId)
    {
        var deleted = s.EndpointIds.Order().ToArray();
        var updated = s.Survivors.Select(x => x.Id).Order().ToArray();
        var places = s.Siblings.Where(p => p.Id != s.Place.Id).OrderBy(p => p.DisplayOrder == null).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Id).ToArray();
        var segments = deleted.Length > 0 ? s.SegmentOrders.Where(x => !deleted.Contains(x.Id)).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).Select(x => x.Id).ToArray() : null;
        return new(1, s.Trip.Id, s.Place.Id, s.Place.RegionId, s.Place.Name, new(deleted.Length, deleted, false),
            new(updated.Length, updated, false), new(s.Detached.Length, s.Detached, false), s.Visits.Length, s.Candidates.Length,
            places, segments, _confirmation.IssuePlaceDeletion(userId, s.Trip.Id, s.Place.Id, PlaceDeletionFingerprint(s)));
    }

}

/// <summary>Persisted consent inputs and the existing lifecycle lock order, including normalized siblings.</summary>
internal sealed record PlaceDeletionSnapshot(Trip Trip, Place Place, Place[] Siblings, Segment[] Segments,
    Segment[] SegmentOrders, Place[] Anchors, TransportProfile[] Profiles, Guid[] EndpointIds, Guid[] Visits, Guid[] Candidates)
{
    /// <summary>Only waypoint-dependent survivors require route reconciliation.</summary>
    internal Segment[] Survivors => Segments.Where(s => !EndpointIds.Contains(s.Id)).ToArray();
    /// <summary>Complete association effects, excluding endpoint Segments which are deleted outright.</summary>
    internal EditorLifecycleWaypointAssociationDto[] Detached => Survivors.SelectMany(s => s.Waypoints.Where(w => w.PlaceId == Place.Id)
        .Select(w => new EditorLifecycleWaypointAssociationDto(s.Id, w.PlaceId))).OrderBy(w => w.SegmentId).ToArray();
    /// <summary>Sorted global lock classes fence affected route, sibling and parent membership.</summary>
    internal LifecycleLockCandidates Locks => new(Profiles.Select(p => p.Id).Order().ToArray(),
        Segments.Concat(SegmentOrders).Select(s => s.Id).Distinct().Order().ToArray(),
        Siblings.Concat(Anchors).Select(p => p.Id).Distinct().Order().ToArray(),
        Anchors.Select(p => p.RegionId).Append(Place.RegionId).Distinct().Order().ToArray());
}
