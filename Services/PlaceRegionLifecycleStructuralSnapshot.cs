using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Reads only the operation's material scope, without ownership-filtering malformed dependencies away.</summary>
    private async Task<PlaceStructuralSnapshot> ReadPlaceStructureAsync(Guid tripId, Guid targetId, string userId,
        PlaceStructuralRequest request, CancellationToken token)
    {
        var trip = await _dbContext.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, token);
        if (trip == null) throw PlaceStructureUnavailable();
        Place? place = null;
        if (request.Operation != "order")
        {
            place = await _dbContext.Places.AsNoTracking().Include(p => p.Region).SingleOrDefaultAsync(p => p.Id == targetId, token);
            if (place == null || place.UserId != userId || place.Region.TripId != tripId || place.Region.UserId != userId) throw PlaceStructureUnavailable();
        }
        var regionIds = request.Operation == "order" ? new[] { targetId }
            : new[] { place!.RegionId, request.Destination ?? place.RegionId }.Distinct().Order().ToArray();
        var regions = await _dbContext.Regions.AsNoTracking().Where(r => regionIds.Contains(r.Id)).OrderBy(r => r.Id).ToArrayAsync(token);
        if (regions.Length != regionIds.Length || regions.Any(r => r.TripId != tripId || r.UserId != userId)) throw PlaceStructureUnavailable();
        if (request.Web && request.Operation == "order" && regions.Any(r => r.Name == "Unassigned Places"))
            throw new TripMetadataMutationRejected(403, "mutation-operation-forbidden", "Places in Unassigned Places cannot be reordered from this editor action.");
        var ordering = request.Operation == "order" || request.Destination.HasValue;
        var siblings = ordering ? await _dbContext.Places.AsNoTracking().Where(p => regionIds.Contains(p.RegionId)).OrderBy(p => p.Id).ToArrayAsync(token) : [];
        if (siblings.Any(p => p.UserId != userId)) throw PlaceStructureMalformed();
        var coordinates = request.Operation == "coordinates" || request.Changes.ValueKind == System.Text.Json.JsonValueKind.Object
            && request.Changes.TryGetProperty("location", out _);
        var segments = coordinates ? await _dbContext.Segments.AsNoTracking().Include(s => s.Waypoints)
            .Where(s => s.FromPlaceId == targetId || s.ToPlaceId == targetId || s.Waypoints.Any(w => w.PlaceId == targetId))
            .OrderBy(s => s.Id).ToArrayAsync(token) : [];
        var anchorIds = segments.SelectMany(s => s.Waypoints.Select(w => w.PlaceId)
            .Concat(new[] { s.FromPlaceId, s.ToPlaceId }.OfType<Guid>())).Distinct().Order().ToArray();
        var anchors = await _dbContext.Places.AsNoTracking().Include(p => p.Region)
            .Where(p => anchorIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        if (segments.Any(s => s.UserId != userId || s.TripId != tripId) || anchors.Length != anchorIds.Length
            || anchors.Any(p => p.UserId != userId || p.Region.UserId != userId || p.Region.TripId != tripId)) throw PlaceStructureMalformed();
        var profileIds = segments.Select(s => s.TransportProfileId).OfType<Guid>().Distinct().ToArray();
        var profiles = await _dbContext.Set<TransportProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        if (profiles.Length != profileIds.Length) throw PlaceStructureMalformed();
        return new(trip, place, regions, siblings, segments, anchors, profiles, coordinates);
    }

    /// <summary>Full moved/reordered content; coordinate dependencies exclude unrelated target/sibling Notes and Trip metadata.</summary>
    private string PlaceStructureFingerprint(PlaceStructuralSnapshot snapshot, PlaceStructuralRequest request)
    {
        object Scalars(object entity) => _dbContext.Model.FindEntityType(entity.GetType())!.GetProperties()
            .Where(p => !p.IsShadowProperty() && p.Name != nameof(Segment.RowVersion)).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new { p.Name, Value = FingerprintValue(p.PropertyInfo!.GetValue(entity)) }).ToArray();
        var place = snapshot.Place;
        return PlaceStructuralRequest.Hash(new
        {
            Target = request.Operation == "order" ? null : request.Destination.HasValue ? Scalars(place!) : new
            {
                place!.Id, place.UserId, place.RegionId, Location = FingerprintValue(place.Location),
                place.ResolvedFeatureName, place.ResolvedFeatureType, place.AddressEnrichmentProvider,
                place.AddressEnrichmentStorageMode, place.AddressEnrichedAt
            },
            Regions = snapshot.Regions.Select(r => new { r.Id, r.TripId, r.UserId, Canonical = r.Name == "Unassigned Places" }),
            Siblings = snapshot.Siblings.Select(p => request.Operation == "order" ? Scalars(p) : (object)new { p.Id, p.UserId, p.RegionId, p.DisplayOrder }),
            Segments = snapshot.Segments.Select(s => new { Values = Scalars(s), Waypoints = s.Waypoints.OrderBy(w => w.Position).ThenBy(w => w.PlaceId).Select(Scalars) }),
            Anchors = snapshot.Anchors.Select(p => new { p.Id, p.UserId, p.RegionId, p.Region.TripId, RegionOwner = p.Region.UserId, Location = FingerprintValue(p.Location) }),
            Profiles = snapshot.Profiles.Select(Scalars)
        });
    }

    private static TripMetadataMutationRejected PlaceStructureUnavailable() => new(404, "mutation-scope-unavailable", "Place or Region not found.");
    private static TripMetadataMutationRejected PlaceStructureMalformed() => new(409, "mutation-dependency-stale", "The affected graph requires review before saving.");
}

/// <summary>One untracked material snapshot with the lifecycle's global lock-class order.</summary>
internal sealed record PlaceStructuralSnapshot(Trip Trip, Place? Place, Region[] Regions, Place[] Siblings,
    Segment[] Segments, Place[] Anchors, TransportProfile[] Profiles, bool Coordinates)
{
    internal LifecycleLockCandidates Locks => new(Profiles.Select(p => p.Id).Order().ToArray(), Segments.Select(s => s.Id).Order().ToArray(),
        Siblings.Concat(Anchors).Concat(Place == null ? [] : new[] { Place }).Select(p => p.Id).Distinct().Order().ToArray(),
        Regions.Select(r => r.Id).Concat(Anchors.Select(p => p.RegionId)).Distinct().Order().ToArray());
}
