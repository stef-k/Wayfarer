using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Reads persisted, untracked operation inputs; callers supply snapshot isolation or held row barriers.</summary>
    private async Task<RegionDeletionSnapshot> ReadRegionDeletionAsync(Guid tripId, Guid sourceId, string userId,
        RegionDeletionPreviewRequestDto choices, Guid proposedId, CancellationToken token)
    {
        var trip = await _dbContext.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, token);
        if (trip == null) throw new RegionDeletionRejectedException(404, "not-found", "Trip or Region not found.");
        var regions = await _dbContext.Regions.AsNoTracking().Where(r => r.TripId == tripId)
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToArrayAsync(token);
        var source = regions.SingleOrDefault(r => r.Id == sourceId && r.UserId == userId);
        if (source == null) throw new RegionDeletionRejectedException(404, "not-found", "Trip or Region not found.");
        if (source.Name == "Unassigned Places")
            throw new RegionDeletionRejectedException(403, "forbidden", "Unassigned Places cannot be deleted.");
        if (regions.Any(r => r.UserId != userId)) throw MalformedDeletionGraph();
        var places = await _dbContext.Places.AsNoTracking().Where(p => p.RegionId == sourceId)
            .OrderBy(p => p.DisplayOrder == null).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToArrayAsync(token);
        var areas = await ReadDeletionAreasAsync(sourceId, token);
        var canonical = regions.Where(r => r.Name == "Unassigned Places").ToArray();
        if (canonical.Length > 1 || canonical.Any(r => r.UserId != userId)) throw MalformedDeletionGraph();
        var needsDestination = (choices.Places == "keep" && places.Length > 0) || (choices.Areas == "keep" && areas.Length > 0);
        var destination = !needsDestination ? new RegionDeletionDestinationDto("not-required", null, null)
            : new RegionDeletionDestinationDto(canonical.Length == 1 ? "existing" : "create",
                canonical.SingleOrDefault()?.Id ?? proposedId, "Unassigned Places");
        var destinationPlaces = destination.Mode == "existing" && choices.Places == "keep" && places.Length > 0
            ? await _dbContext.Places.AsNoTracking().Where(p => p.RegionId == destination.RegionId)
                .OrderBy(p => p.DisplayOrder == null).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToArrayAsync(token) : [];
        var destinationAreas = destination.Mode == "existing" && choices.Areas == "keep" && areas.Length > 0
            ? await ReadDeletionAreasAsync(destination.RegionId!.Value, token) : [];
        var placeIds = places.Select(p => p.Id).ToArray();
        // Do not ownership-filter dependencies: malformed foreign anchors must fail closed, not disappear.
        var segments = await _dbContext.Segments.AsNoTracking().Include(s => s.Waypoints)
            .Where(s => placeIds.Contains(s.FromPlaceId!.Value) || placeIds.Contains(s.ToPlaceId!.Value)
                || s.Waypoints.Any(w => placeIds.Contains(w.PlaceId)))
            .OrderBy(s => s.Id).ToArrayAsync(token);
        var endpointIds = segments.Where(s => placeIds.Contains(s.FromPlaceId ?? Guid.Empty)
            || placeIds.Contains(s.ToPlaceId ?? Guid.Empty)).Select(s => s.Id).ToArray();
        var segmentOrders = choices.Places == "delete" && endpointIds.Length > 0
            ? await _dbContext.Segments.AsNoTracking().Where(s => s.TripId == tripId)
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToArrayAsync(token) : [];
        var anchorIds = segments.SelectMany(s => s.Waypoints.Select(w => w.PlaceId)
            .Concat(new[] { s.FromPlaceId, s.ToPlaceId }.OfType<Guid>())).Distinct().ToArray();
        var anchors = await _dbContext.Places.AsNoTracking().Include(p => p.Region)
            .Where(p => anchorIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        if (places.Concat(destinationPlaces).Any(p => p.UserId != userId)
            || segments.Concat(segmentOrders).Any(s => s.UserId != userId || s.TripId != tripId)
            || anchors.Length != anchorIds.Length
            || anchors.Any(p => p.UserId != userId || p.Region.UserId != userId || p.Region.TripId != tripId))
            throw MalformedDeletionGraph();
        var profileIds = segments.Where(s => s.TransportProfileId.HasValue).Select(s => s.TransportProfileId!.Value).Distinct().ToArray();
        var profiles = await _dbContext.Set<TransportProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).OrderBy(p => p.Id).ToArrayAsync(token);
        return new(trip, source, regions, places, areas, destination, destinationPlaces, destinationAreas,
            segments, segmentOrders, anchors, profiles, endpointIds);
    }

    /// <summary>Uses database collation for equal-order Area names, then a stable identity tie-breaker.</summary>
    private Task<Area[]> ReadDeletionAreasAsync(Guid regionId, CancellationToken token) =>
        _dbContext.Areas.AsNoTracking().Where(a => a.RegionId == regionId)
            .OrderBy(a => a.DisplayOrder == null).ThenBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).ToArrayAsync(token);

    private static RegionDeletionRejectedException MalformedDeletionGraph() => new(409, "unassigned_region_conflict",
        "The affected graph requires reconciliation. Refresh and contact the maintainer if this persists.");

    /// <summary>Hashes exact mapped scalar contents, preserving geometry SRID/rings and all route provenance fields.</summary>
    private string RegionDeletionFingerprint(RegionDeletionSnapshot snapshot)
    {
        object Scalars(object entity) => _dbContext.Model.FindEntityType(entity.GetType())!.GetProperties()
            .Where(p => !p.IsShadowProperty()).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new { p.Name, Value = FingerprintValue(p.PropertyInfo!.GetValue(entity)) }).ToArray();
        var scope = new
        {
            Source = Scalars(snapshot.Source),
            Places = snapshot.Places.Select(Scalars), Areas = snapshot.Areas.Select(Scalars),
            Segments = snapshot.Segments.Select(s => new { Values = Scalars(s),
                Waypoints = s.Waypoints.OrderBy(w => w.Position).ThenBy(w => w.PlaceId).Select(Scalars).ToArray() }),
            Anchors = snapshot.Anchors.Select(p => new { p.Id, p.UserId, p.RegionId, Location = FingerprintValue(p.Location) }),
            Profiles = snapshot.Profiles.Select(Scalars), snapshot.Destination,
            DestinationPlaces = snapshot.DestinationPlaces.Select(p => new { p.Id, p.RegionId, p.UserId, p.DisplayOrder }),
            DestinationAreas = snapshot.DestinationAreas.Select(a => new { a.Id, a.RegionId, a.DisplayOrder, a.Name }),
            Regions = snapshot.Regions.Select(r => new { r.Id, r.TripId, r.UserId, r.DisplayOrder, Canonical = r.Name == "Unassigned Places" }),
            SegmentOrder = snapshot.SegmentOrders.Select(s => new { s.Id, s.UserId, s.TripId, s.DisplayOrder })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope))));
    }

    private static object? FingerprintValue(object? value) => value is Geometry geometry
        ? new { geometry.SRID, Wkb = Convert.ToBase64String(geometry.AsBinary()) } : value;

    /// <summary>Builds complete counts and deterministic samples without exposing child content.</summary>
    private RegionDeletionPreviewDto BuildRegionDeletionPreview(RegionDeletionSnapshot s, string userId, RegionDeletionPreviewRequestDto choices)
    {
        var deleting = choices.Places == "delete";
        var endpoint = deleting ? s.EndpointIds : [];
        var surviving = deleting ? s.Segments.Where(x => !endpoint.Contains(x.Id)).ToArray() : [];
        var placeIds = s.Places.Select(p => p.Id).ToArray();
        var associations = surviving.SelectMany(x => x.Waypoints.Where(w => placeIds.Contains(w.PlaceId))
            .Select(w => new EditorLifecycleWaypointAssociationDto(x.Id, w.PlaceId)))
            .OrderBy(w => w.SegmentId).ThenBy(w => w.PlaceId).ToArray();
        var effects = new RegionDeletionConsequencesDto(
            DeletionSample(choices.Places == "keep" ? placeIds : []), DeletionSample(deleting ? placeIds : []),
            DeletionSample(choices.Areas == "keep" ? s.Areas.Select(a => a.Id) : []),
            DeletionSample(choices.Areas == "delete" ? s.Areas.Select(a => a.Id) : []),
            DeletionSample(endpoint), DeletionSample(surviving.Select(x => x.Id)),
            new(associations.Length, associations.Take(5).ToArray(), associations.Length > 5));
        return new(1, s.Trip.Id, s.Source.Id, s.Source.Name, choices.Places, choices.Areas, s.Destination, effects,
            new(!string.IsNullOrWhiteSpace(s.Source.Notes), !string.IsNullOrWhiteSpace(s.Source.CoverImageUrl), s.Source.Center != null),
            _confirmation.IssueRegionDeletion(userId, s.Trip.Id, s.Source.Id, choices.Places, choices.Areas,
                s.Destination, RegionDeletionFingerprint(s)));
    }

    private static EditorLifecycleDependencySampleDto DeletionSample(IEnumerable<Guid> ids)
    {
        var ordered = ids.Distinct().Order().ToArray();
        return new(ordered.Length, ordered.Take(5).ToArray(), ordered.Length > 5);
    }
}

/// <summary>Untracked persisted inputs for one Region deletion approval/locking scope.</summary>
internal sealed record RegionDeletionSnapshot(Trip Trip, Region Source, Region[] Regions, Place[] Places, Area[] Areas,
    RegionDeletionDestinationDto Destination, Place[] DestinationPlaces, Area[] DestinationAreas, Segment[] Segments,
    Segment[] SegmentOrders, Place[] Anchors, TransportProfile[] Profiles, Guid[] EndpointIds)
{
    /// <summary>All earlier-class rows needed before Region/Trip membership barriers.</summary>
    internal LifecycleLockCandidates Locks => new(Profiles.Select(p => p.Id).Order().ToArray(),
        Segments.Concat(SegmentOrders).Select(s => s.Id).Distinct().Order().ToArray(),
        Places.Concat(DestinationPlaces).Concat(Anchors).Select(p => p.Id).Distinct().Order().ToArray(),
        Regions.Select(r => r.Id).Order().ToArray());
    /// <summary>Area rows must be locked before Region parent barriers.</summary>
    internal Guid[] AreaIds => Areas.Concat(DestinationAreas).Select(a => a.Id).Distinct().Order().ToArray();
}
