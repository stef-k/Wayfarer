using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>Owns atomic, waypoint-aware mutations of existing Places and Regions.</summary>
public sealed partial class PlaceRegionLifecycleService
{
    private const int MaximumUpdateAttempts = 3;
    private const string PlaceDeleteOperation = "place-delete";
    private const string RegionDeleteOperation = "region-delete";
    private readonly ApplicationDbContext _dbContext;
    private readonly LifecycleDependencyConfirmation _confirmation;

    /// <summary>Initializes the lifecycle boundary.</summary>
    public PlaceRegionLifecycleService(ApplicationDbContext dbContext, LifecycleDependencyConfirmation confirmation)
    {
        _dbContext = dbContext;
        _confirmation = confirmation;
    }

    /// <summary>Atomically updates Place scalar state, Region membership, routes, measurements, and orders.</summary>
    public async Task<PlaceLifecycleUpdateResult> UpdatePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        PlaceLifecycleUpdate update,
        CancellationToken cancellationToken)
    {
        var trackerSnapshot = LifecycleTrackerSnapshot.Capture(_dbContext);
        for (var attempt = 1; attempt <= MaximumUpdateAttempts; attempt++)
        {
            var candidates = await DiscoverUpdateCandidatesAsync(tripId, placeId, update.RegionId, userId, cancellationToken);
            if (candidates == null) return PlaceLifecycleUpdateResult.NotFound;
            var recovery = new LifecycleRecoveryScope([placeId], candidates.RegionIds, candidates.SegmentIds);
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            try
            {
                await LockAsync(candidates, cancellationToken);
                var canonicalCandidates = await DiscoverUpdateCandidatesAsync(tripId, placeId, update.RegionId, userId, cancellationToken);
                if (canonicalCandidates == null) return PlaceLifecycleUpdateResult.NotFound;
                if (canonicalCandidates.RequiresLocksOutside(candidates))
                {
                    await RollbackAsync(transaction);
                    if (attempt == MaximumUpdateAttempts) return PlaceLifecycleUpdateResult.ConcurrencyConflict;
                    continue;
                }

                DetachCanonicalEntries(canonicalCandidates);
                var place = await LoadOwnedPlaceAsync(tripId, placeId, userId, cancellationToken);
                if (place == null) return PlaceLifecycleUpdateResult.NotFound;
                var targetRegion = await _dbContext.Regions
                    .SingleOrDefaultAsync(region => region.Id == update.RegionId && region.TripId == tripId && region.UserId == userId, cancellationToken);
                if (targetRegion == null) return PlaceLifecycleUpdateResult.NotFound;
                var affected = await LoadAffectedSegmentsAsync(tripId, userId, [placeId], cancellationToken);
                recovery = new([placeId], [place.RegionId, targetRegion.Id], affected.Select(item => item.Id).ToArray());
                var oldRegionId = place.RegionId;
                var moved = oldRegionId != targetRegion.Id;
                var locationChanged = !CoordinatesEqual(place.Location, update.Location);
                if (update.Location == null && affected.Any(segment => segment.Waypoints.Count > 0))
                {
                    await RollbackAsync(transaction);
                    return PlaceLifecycleUpdateResult.Validation("location", "waypoint-location-required", "Location is required while the Place is referenced by a waypoint-bearing Segment.");
                }

                place.Name = update.Name;
                place.Notes = update.Notes;
                place.Address = update.Address;
                place.IconName = update.IconName;
                place.MarkerColor = update.MarkerColor;
                place.Location = CopyPoint(update.Location);
                if (moved)
                {
                    place.RegionId = targetRegion.Id;
                    place.Region = targetRegion;
                    place.DisplayOrder = await NextPlaceOrderAsync(targetRegion.Id, cancellationToken);
                }
                if (update.DisplayOrder.HasValue) place.DisplayOrder = update.DisplayOrder.Value;

                if (locationChanged)
                {
                    foreach (var segment in affected.OrderBy(item => item.Id))
                    {
                        RewriteLocation(segment, placeId, update.Location);
                        await ReconcileSegmentAsync(segment, cancellationToken);
                    }
                }

                if (moved)
                {
                    await NormalizePlaceOrdersAsync(oldRegionId, placeId, cancellationToken);
                    await NormalizePlaceOrdersAsync(targetRegion.Id, null, cancellationToken);
                }
                else if (update.DisplayOrder.HasValue)
                {
                    await NormalizePlaceOrdersAsync(targetRegion.Id, null, cancellationToken);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                var orderRegions = moved ? new[] { oldRegionId, targetRegion.Id }
                    : update.DisplayOrder.HasValue ? new[] { targetRegion.Id } : [];
                return new(true, null, null, place, affected, orderRegions, locationChanged);
            }
            catch (Exception original) when (attempt < MaximumUpdateAttempts && IsSerializationFailure(original))
            {
                await RecoverAsync(original, transaction, recovery, trackerSnapshot);
                continue;
            }
            catch (Exception original)
            {
                await RecoverAndRethrowAsync(original, transaction, recovery, trackerSnapshot);
                throw;
            }
        }

        return PlaceLifecycleUpdateResult.ConcurrencyConflict;
    }

    /// <summary>Deletes a Place and all endpoint/waypoint effects after server-owned confirmation.</summary>
    public async Task<PlaceLifecycleDeleteResult> DeletePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        var trackerSnapshot = LifecycleTrackerSnapshot.Capture(_dbContext);
        var recovery = new LifecycleRecoveryScope([placeId], [], []);
        var dependencies = await DiscoverPlaceDependenciesAsync(tripId, placeId, userId, cancellationToken);
        if (dependencies == null) return PlaceLifecycleDeleteResult.NotFound;
        var warning = _confirmation.Create("place-delete-dependencies", PlaceDeleteOperation, userId, tripId, placeId, dependencies);
        if (dependencies.RequiresConfirmation && !_confirmation.IsValid(confirmationToken, PlaceDeleteOperation, userId, tripId, placeId, dependencies))
            return PlaceLifecycleDeleteResult.Conflict(warning with { Code = string.IsNullOrWhiteSpace(confirmationToken) ? warning.Code : "lifecycle-confirmation-stale" });
        var regionId = await _dbContext.Places.AsNoTracking()
            .Where(place => place.Id == placeId && place.UserId == userId && place.Region.TripId == tripId)
            .Select(place => place.RegionId)
            .SingleAsync(cancellationToken);
        var candidates = await BuildDeleteLockCandidatesAsync(dependencies, [placeId], [regionId], cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken, IsolationLevel.ReadCommitted);
        try
        {
            await LockAsync(candidates, cancellationToken);
            var canonical = await DiscoverPlaceDependenciesAsync(tripId, placeId, userId, cancellationToken);
            if (canonical == null) return PlaceLifecycleDeleteResult.NotFound;
            var requiredLocks = await BuildDeleteLockCandidatesAsync(canonical, [placeId], [regionId], cancellationToken);
            if (dependencies.Fingerprint() != canonical.Fingerprint() || requiredLocks.RequiresLocksOutside(candidates))
            {
                await RollbackAsync(transaction);
                RestoreTracker(recovery, trackerSnapshot);
                var current = await DiscoverPlaceDependenciesAsync(tripId, placeId, userId, CancellationToken.None);
                if (current == null) return PlaceLifecycleDeleteResult.NotFound;
                var stale = _confirmation.Create("lifecycle-confirmation-stale", PlaceDeleteOperation, userId, tripId, placeId, current);
                return PlaceLifecycleDeleteResult.Conflict(stale);
            }
            DetachCanonicalEntries(candidates);
            var affected = await LoadAffectedSegmentsAsync(tripId, userId, [placeId], cancellationToken);
            var place = await LoadOwnedPlaceAsync(tripId, placeId, userId, cancellationToken);
            if (place == null) return PlaceLifecycleDeleteResult.NotFound;
            recovery = new([placeId], [place.RegionId], affected.Select(item => item.Id).ToArray());

            var endpointIds = canonical.EndpointSegmentIds.ToHashSet();
            var deletedSegments = affected.Where(segment => endpointIds.Contains(segment.Id)).ToArray();
            var surviving = affected.Where(segment => !endpointIds.Contains(segment.Id)).OrderBy(segment => segment.Id).ToArray();
            _dbContext.Segments.RemoveRange(deletedSegments);
            foreach (var segment in surviving)
            {
                await ReconcileAfterWaypointDeletionAsync(segment, new HashSet<Guid> { placeId }, cancellationToken);
            }

            regionId = place.RegionId;
            _dbContext.Places.Remove(place);
            await NormalizePlaceOrdersAsync(regionId, placeId, cancellationToken);
            await NormalizeSegmentOrdersAsync(tripId, userId, endpointIds, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new(true, null, placeId, regionId, endpointIds.Order().ToArray(), surviving);
        }
        catch (Exception original)
        {
            await RecoverAndRethrowAsync(original, transaction, recovery, trackerSnapshot);
            throw;
        }
    }

    /// <summary>Discovers bounded canonical Place dependencies without tracking private aggregates.</summary>
    public async Task<LifecycleDependencies?> DiscoverPlaceDependenciesAsync(Guid tripId, Guid placeId, string userId, CancellationToken cancellationToken)
    {
        var owned = await _dbContext.Places.AsNoTracking()
            .AnyAsync(place => place.Id == placeId && place.UserId == userId && place.Region.TripId == tripId, cancellationToken);
        if (!owned) return null;
        var endpoint = await _dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TripId == tripId && segment.UserId == userId && (segment.FromPlaceId == placeId || segment.ToPlaceId == placeId))
            .Select(segment => segment.Id).Distinct().Order().ToArrayAsync(cancellationToken);
        var associations = await _dbContext.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => item.PlaceId == placeId && item.Segment.TripId == tripId && item.Segment.UserId == userId)
            .Select(item => new { item.SegmentId, item.PlaceId }).OrderBy(item => item.SegmentId).ToArrayAsync(cancellationToken);
        var waypointOnly = associations.Select(item => item.SegmentId).Except(endpoint).Distinct().Order().ToArray();
        return new(endpoint, waypointOnly, associations.Select(item => (item.SegmentId, item.PlaceId)).ToArray(), [], []);
    }

    /// <summary>Deletes a normal Region and all mixed endpoint/waypoint dependencies after confirmation.</summary>
    public async Task<RegionLifecycleDeleteResult> DeleteRegionAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        var trackerSnapshot = LifecycleTrackerSnapshot.Capture(_dbContext);
        var recovery = new LifecycleRecoveryScope([], [regionId], []);
        var dependencies = await DiscoverRegionDependenciesAsync(tripId, regionId, userId, cancellationToken);
        if (dependencies == null) return RegionLifecycleDeleteResult.NotFound;
        var warning = _confirmation.Create("region-delete-dependencies", RegionDeleteOperation, userId, tripId, regionId, dependencies);
        if (dependencies.RequiresConfirmation && !_confirmation.IsValid(confirmationToken, RegionDeleteOperation, userId, tripId, regionId, dependencies))
            return RegionLifecycleDeleteResult.Conflict(warning with { Code = string.IsNullOrWhiteSpace(confirmationToken) ? warning.Code : "lifecycle-confirmation-stale" });
        var candidates = await BuildDeleteLockCandidatesAsync(dependencies, dependencies.PlaceIds.ToArray(), [regionId], cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken, IsolationLevel.ReadCommitted);
        try
        {
            await LockAsync(candidates, cancellationToken);
            var canonical = await DiscoverRegionDependenciesAsync(tripId, regionId, userId, cancellationToken);
            if (canonical == null) return RegionLifecycleDeleteResult.NotFound;
            var requiredLocks = await BuildDeleteLockCandidatesAsync(
                canonical, canonical.PlaceIds.ToArray(), [regionId], cancellationToken);
            if (canonical.Fingerprint() != dependencies.Fingerprint() || requiredLocks.RequiresLocksOutside(candidates))
            {
                await RollbackAsync(transaction);
                RestoreTracker(recovery, trackerSnapshot);
                var current = await DiscoverRegionDependenciesAsync(tripId, regionId, userId, CancellationToken.None);
                if (current == null) return RegionLifecycleDeleteResult.NotFound;
                return RegionLifecycleDeleteResult.Conflict(
                    _confirmation.Create("lifecycle-confirmation-stale", RegionDeleteOperation, userId, tripId, regionId, current));
            }
            DetachCanonicalEntries(candidates);
            var region = await _dbContext.Regions.Include(item => item.Places).Include(item => item.Areas)
                .SingleOrDefaultAsync(item => item.Id == regionId && item.TripId == tripId && item.UserId == userId, cancellationToken);
            if (region == null || region.Name == "Unassigned Places") return RegionLifecycleDeleteResult.NotFound;
            var placeIds = region.Places.Select(item => item.Id).Order().ToArray();
            var affected = placeIds.Length == 0 ? [] : await LoadAffectedSegmentsAsync(tripId, userId, placeIds, cancellationToken);
            recovery = new(placeIds, [regionId], affected.Select(item => item.Id).ToArray());

            var endpointIds = canonical.EndpointSegmentIds.ToHashSet();
            _dbContext.Segments.RemoveRange(affected.Where(item => endpointIds.Contains(item.Id)));
            var surviving = affected.Where(item => !endpointIds.Contains(item.Id)).OrderBy(item => item.Id).ToArray();
            var deletedPlaces = placeIds.ToHashSet();
            foreach (var segment in surviving)
            {
                await ReconcileAfterWaypointDeletionAsync(segment, deletedPlaces, cancellationToken);
            }
            var areaIds = region.Areas.Select(item => item.Id).Order().ToArray();
            _dbContext.Areas.RemoveRange(region.Areas);
            _dbContext.Places.RemoveRange(region.Places);
            _dbContext.Regions.Remove(region);
            await NormalizeSegmentOrdersAsync(tripId, userId, endpointIds, cancellationToken);
            await NormalizeRegionOrdersAsync(tripId, userId, regionId, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new(true, null, regionId, placeIds, areaIds, endpointIds.Order().ToArray(), surviving);
        }
        catch (Exception original)
        {
            await RecoverAndRethrowAsync(original, transaction, recovery, trackerSnapshot);
            throw;
        }
    }

    /// <summary>Discovers canonical children and Segment roles for a normal owned Region.</summary>
    public async Task<LifecycleDependencies?> DiscoverRegionDependenciesAsync(Guid tripId, Guid regionId, string userId, CancellationToken cancellationToken)
    {
        var region = await _dbContext.Regions.AsNoTracking()
            .Where(item => item.Id == regionId && item.TripId == tripId && item.UserId == userId && item.Name != "Unassigned Places")
            .Select(item => new
            {
                PlaceIds = item.Places.Select(place => place.Id).OrderBy(id => id).ToArray(),
                AreaIds = item.Areas.Select(area => area.Id).OrderBy(id => id).ToArray()
            }).SingleOrDefaultAsync(cancellationToken);
        if (region == null) return null;
        var endpoint = await _dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TripId == tripId && segment.UserId == userId
                && (region.PlaceIds.Contains(segment.FromPlaceId!.Value) || region.PlaceIds.Contains(segment.ToPlaceId!.Value)))
            .Select(segment => segment.Id).Distinct().Order().ToArrayAsync(cancellationToken);
        var associations = await _dbContext.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => region.PlaceIds.Contains(item.PlaceId) && item.Segment.TripId == tripId && item.Segment.UserId == userId)
            .Select(item => new { item.SegmentId, item.PlaceId }).OrderBy(item => item.SegmentId).ThenBy(item => item.PlaceId).ToArrayAsync(cancellationToken);
        return new(endpoint, associations.Select(item => item.SegmentId).Except(endpoint).Distinct().Order().ToArray(),
            associations.Select(item => (item.SegmentId, item.PlaceId)).ToArray(), region.PlaceIds, region.AreaIds);
    }

    private async Task<IReadOnlyList<Segment>> LoadAffectedSegmentsAsync(Guid tripId, string userId, Guid[] placeIds, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .Include(segment => segment.FromPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.ToPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.Waypoints.OrderBy(item => item.Position)).ThenInclude(item => item.Place).ThenInclude(place => place.Region)
            .Where(segment => segment.TripId == tripId && segment.UserId == userId
                && (placeIds.Contains(segment.FromPlaceId!.Value) || placeIds.Contains(segment.ToPlaceId!.Value) || segment.Waypoints.Any(item => placeIds.Contains(item.PlaceId))))
            .OrderBy(segment => segment.Id).ToArrayAsync(cancellationToken);

    private async Task<LifecycleLockCandidates?> DiscoverUpdateCandidatesAsync(
        Guid tripId,
        Guid placeId,
        Guid targetRegionId,
        string userId,
        CancellationToken cancellationToken)
    {
        var currentRegionId = await _dbContext.Places.AsNoTracking()
            .Where(place => place.Id == placeId && place.UserId == userId && place.Region.TripId == tripId)
            .Select(place => (Guid?)place.RegionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!currentRegionId.HasValue) return null;
        var targetExists = await _dbContext.Regions.AsNoTracking()
            .AnyAsync(region => region.Id == targetRegionId && region.TripId == tripId && region.UserId == userId, cancellationToken);
        if (!targetExists) return null;
        var segments = await _dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TripId == tripId && segment.UserId == userId
                && (segment.FromPlaceId == placeId || segment.ToPlaceId == placeId || segment.Waypoints.Any(item => item.PlaceId == placeId)))
            .Select(segment => new { segment.Id, segment.TransportProfileId })
            .OrderBy(segment => segment.Id)
            .ToArrayAsync(cancellationToken);
        return new(
            segments.Where(item => item.TransportProfileId.HasValue).Select(item => item.TransportProfileId!.Value).Distinct().Order().ToArray(),
            segments.Select(item => item.Id).Distinct().Order().ToArray(),
            [placeId],
            new[] { currentRegionId.Value, targetRegionId }.Distinct().Order().ToArray());
    }

    private async Task<LifecycleLockCandidates> BuildDeleteLockCandidatesAsync(
        LifecycleDependencies dependencies,
        Guid[] placeIds,
        Guid[] regionIds,
        CancellationToken cancellationToken)
    {
        var segmentIds = dependencies.EndpointSegmentIds.Concat(dependencies.WaypointOnlySegmentIds).Distinct().Order().ToArray();
        var profileIds = await _dbContext.Segments.AsNoTracking()
            .Where(segment => segmentIds.Contains(segment.Id) && segment.TransportProfileId.HasValue)
            .Select(segment => segment.TransportProfileId!.Value)
            .Distinct()
            .Order()
            .ToArrayAsync(cancellationToken);
        return new(profileIds, segmentIds, placeIds.Distinct().Order().ToArray(), regionIds.Distinct().Order().ToArray());
    }

    private void DetachCanonicalEntries(LifecycleLockCandidates candidates)
    {
        var segmentIds = candidates.SegmentIds.ToHashSet();
        var placeIds = candidates.PlaceIds.ToHashSet();
        var regionIds = candidates.RegionIds.ToHashSet();
        foreach (var entry in _dbContext.ChangeTracker.Entries().Where(entry => entry.Entity switch
        {
            Segment segment => segmentIds.Contains(segment.Id),
            SegmentWaypoint waypoint => segmentIds.Contains(waypoint.SegmentId),
            Place place => placeIds.Contains(place.Id),
            Region region => regionIds.Contains(region.Id),
            _ => false
        }).ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<Place?> LoadOwnedPlaceAsync(Guid tripId, Guid placeId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Places.Include(place => place.Region)
            .SingleOrDefaultAsync(place => place.Id == placeId && place.UserId == userId && place.Region.TripId == tripId, cancellationToken);

    private async Task ReconcileSegmentAsync(Segment segment, CancellationToken cancellationToken)
    {
        if (segment.Waypoints.Count == 0)
        {
            await SegmentRouteReconciler.ReconcileTrackedMeasurementsAsync(_dbContext, segment, cancellationToken);
            return;
        }
        var proposal = new SegmentRouteProposal(segment.Id, segment.FromPlaceId, segment.ToPlaceId,
            segment.Waypoints.OrderBy(item => item.Position)
                .Select((item, position) => new SegmentWaypointProposal(item.PlaceId, position, item.RouteVertexIndex)).ToArray(),
            segment.RouteGeometry,
            new(segment.Mode, segment.TransportProfileId, segment.EstimatedDurationSource,
                segment.EstimatedDurationSource == EstimatedDurationSource.Manual ? segment.EstimatedDuration?.TotalMinutes : null, true));
        var result = await SegmentRouteReconciler.ReconcileLockedAsync(_dbContext, proposal, false, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors));
    }

    private static void RewriteLocation(Segment segment, Guid placeId, Point? location)
    {
        if (segment.RouteGeometry == null) return;
        if (location == null)
        {
            segment.RouteGeometry = null;
            foreach (var waypoint in segment.Waypoints) waypoint.RouteVertexIndex = null;
            return;
        }

        var coordinates = segment.RouteGeometry.Coordinates.ToArray();
        if (coordinates.Length < 2) throw new InvalidOperationException("Canonical custom route must contain at least two vertices.");
        var replacement = new Coordinate(location.X, location.Y);
        if (segment.FromPlaceId == placeId) coordinates[0] = replacement.Copy();
        if (segment.ToPlaceId == placeId) coordinates[^1] = replacement.Copy();
        foreach (var waypoint in segment.Waypoints.Where(item => item.PlaceId == placeId))
        {
            if (!waypoint.RouteVertexIndex.HasValue || waypoint.RouteVertexIndex < 1 || waypoint.RouteVertexIndex >= coordinates.Length - 1)
                throw new InvalidOperationException("Canonical waypoint route index is invalid.");
            coordinates[waypoint.RouteVertexIndex.Value] = replacement.Copy();
        }
        segment.RouteGeometry = new LineString(coordinates) { SRID = 4326 };
    }

    private async Task ReconcileAfterWaypointDeletionAsync(
        Segment segment,
        IReadOnlySet<Guid> deletedPlaceIds,
        CancellationToken cancellationToken)
    {
        var canonicalErrors = await SegmentRouteReconciler.ValidateLockedAggregateAsync(
            _dbContext, segment, cancellationToken);
        if (canonicalErrors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", canonicalErrors));

        var survivingWaypoints = segment.Waypoints.OrderBy(item => item.Position)
            .Where(item => !deletedPlaceIds.Contains(item.PlaceId))
            .Select((item, position) => new SegmentWaypointProposal(
                item.PlaceId, position, item.RouteVertexIndex))
            .ToArray();
        var proposal = new SegmentRouteProposal(
            segment.Id,
            segment.FromPlaceId,
            segment.ToPlaceId,
            survivingWaypoints,
            segment.RouteGeometry,
            new(segment.Mode, segment.TransportProfileId, segment.EstimatedDurationSource,
                segment.EstimatedDurationSource == EstimatedDurationSource.Manual
                    ? segment.EstimatedDuration?.TotalMinutes
                    : null,
                true));
        var result = await SegmentRouteReconciler.ReconcileLockedAsync(
            _dbContext, proposal, false, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors));
    }

    private async Task LockAsync(IReadOnlyList<Segment> segments, Guid[] placeIds, Guid[] regionIds, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational()) return;
        var profileIds = segments.Where(item => item.TransportProfileId.HasValue).Select(item => item.TransportProfileId!.Value).Distinct().Order().ToArray();
        await SegmentRouteReconciler.LockProfilesAsync(_dbContext, profileIds, cancellationToken);
        foreach (var segmentId in segments.Select(item => item.Id).Distinct().Order()) await SegmentRouteReconciler.LockSegmentAsync(_dbContext, segmentId, cancellationToken);
        foreach (var placeId in placeIds.Distinct().Order())
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {placeId} FOR UPDATE", cancellationToken);
        foreach (var regionId in regionIds.Distinct().Order())
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {regionId} FOR UPDATE", cancellationToken);
    }

    private async Task LockAsync(LifecycleLockCandidates candidates, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational()) return;
        await SegmentRouteReconciler.LockProfilesAsync(_dbContext, candidates.ProfileIds, cancellationToken);
        foreach (var segmentId in candidates.SegmentIds)
            await SegmentRouteReconciler.LockSegmentAsync(_dbContext, segmentId, cancellationToken);
        foreach (var placeId in candidates.PlaceIds)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {placeId} FOR UPDATE", cancellationToken);
        foreach (var regionId in candidates.RegionIds)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {regionId} FOR UPDATE", cancellationToken);
    }

    private async Task NormalizePlaceOrdersAsync(Guid regionId, Guid? excludedPlaceId, CancellationToken cancellationToken)
    {
        var places = await _dbContext.Places.Where(place => place.RegionId == regionId && place.Id != excludedPlaceId)
            .OrderBy(place => place.DisplayOrder == null).ThenBy(place => place.DisplayOrder).ThenBy(place => place.Id).ToArrayAsync(cancellationToken);
        for (var index = 0; index < places.Length; index++) places[index].DisplayOrder = index + 1;
    }

    private async Task NormalizeSegmentOrdersAsync(Guid tripId, string userId, IReadOnlySet<Guid> excludedIds, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments.Where(segment => segment.TripId == tripId && segment.UserId == userId && !excludedIds.Contains(segment.Id))
            .OrderBy(segment => segment.DisplayOrder).ThenBy(segment => segment.Id).ToArrayAsync(cancellationToken);
        for (var index = 0; index < segments.Length; index++) segments[index].DisplayOrder = index + 1;
    }

    private async Task NormalizeRegionOrdersAsync(Guid tripId, string userId, Guid excludedId, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions.Where(region => region.TripId == tripId && region.UserId == userId && region.Id != excludedId)
            .OrderBy(region => region.Name == "Unassigned Places" ? 0 : 1).ThenBy(region => region.DisplayOrder).ThenBy(region => region.Id)
            .ToArrayAsync(cancellationToken);
        var normalOrder = 1;
        foreach (var region in regions) region.DisplayOrder = region.Name == "Unassigned Places" ? 0 : normalOrder++;
    }

    private async Task<int> NextPlaceOrderAsync(Guid regionId, CancellationToken cancellationToken) =>
        (await _dbContext.Places.Where(place => place.RegionId == regionId).MaxAsync(place => (int?)place.DisplayOrder, cancellationToken) ?? 0) + 1;

    private async Task<IDbContextTransaction?> BeginTransactionAsync(
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.Serializable) =>
        _dbContext.Database.IsRelational() ? await _dbContext.Database.BeginTransactionAsync(isolationLevel, cancellationToken) : null;

    private static async Task RollbackAsync(IDbContextTransaction? transaction)
    {
        if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
    }

    private static Point? CopyPoint(Point? point) => point == null ? null : (Point)point.Copy();
    private static bool CoordinatesEqual(Point? left, Point? right) => left == null && right == null
        || left != null && right != null && left.X.Equals(right.X) && left.Y.Equals(right.Y);

    private static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }) return true;
        }
        return false;
    }
}

/// <summary>Allowlisted scalar state for one existing Place update.</summary>
public sealed record PlaceLifecycleUpdate(Guid RegionId, string Name, string Notes, string Address, string IconName, string MarkerColor, Point? Location, int? DisplayOrder = null);

/// <summary>Result of an atomic Place update.</summary>
public sealed record PlaceLifecycleUpdateResult(bool Succeeded, Dictionary<string, string[]>? Errors, string? ErrorCode, Place? Place, IReadOnlyList<Segment> Segments, IReadOnlyList<Guid> OrderRegionIds, bool LocationChanged)
{
    /// <summary>Represents an ownership-masked missing target.</summary>
    public static PlaceLifecycleUpdateResult NotFound { get; } = new(false, null, null, null, [], [], false);
    /// <summary>Represents deterministic field validation.</summary>
    public static PlaceLifecycleUpdateResult Validation(string field, string code, string message) => new(false, new() { [field] = [message] }, code, null, [], [], false);
    /// <summary>Represents bounded dependency drift after all permitted attempts.</summary>
    public static PlaceLifecycleUpdateResult ConcurrencyConflict { get; } = new(false, null, "lifecycle-concurrency-conflict", null, [], [], false);
}

/// <summary>Sorted identities that one lifecycle attempt must lock before canonical reload.</summary>
internal sealed record LifecycleLockCandidates(
    Guid[] ProfileIds,
    Guid[] SegmentIds,
    Guid[] PlaceIds,
    Guid[] RegionIds)
{
    /// <summary>Returns whether canonical state requires any lock absent from the candidate attempt.</summary>
    internal bool RequiresLocksOutside(LifecycleLockCandidates held) =>
        ProfileIds.Except(held.ProfileIds).Any()
        || SegmentIds.Except(held.SegmentIds).Any()
        || PlaceIds.Except(held.PlaceIds).Any()
        || RegionIds.Except(held.RegionIds).Any();
}

/// <summary>Result of an atomic Place deletion.</summary>
public sealed record PlaceLifecycleDeleteResult(bool Succeeded, EditorLifecycleConflictDto? Warning, Guid? PlaceId, Guid? RegionId, IReadOnlyList<Guid> DeletedSegmentIds, IReadOnlyList<Segment> SurvivingSegments)
{
    /// <summary>Represents an ownership-masked missing target.</summary>
    public static PlaceLifecycleDeleteResult NotFound { get; } = new(false, null, null, null, [], []);
    /// <summary>Represents confirmation-required or stale confirmation.</summary>
    public static PlaceLifecycleDeleteResult Conflict(EditorLifecycleConflictDto warning) => new(false, warning, null, null, [], []);
}

/// <summary>Result of an atomic Region deletion.</summary>
public sealed record RegionLifecycleDeleteResult(bool Succeeded, EditorLifecycleConflictDto? Warning, Guid? RegionId, IReadOnlyList<Guid> PlaceIds, IReadOnlyList<Guid> AreaIds, IReadOnlyList<Guid> SegmentIds, IReadOnlyList<Segment> SurvivingSegments)
{
    /// <summary>Represents an ownership-masked missing or protected Region.</summary>
    public static RegionLifecycleDeleteResult NotFound { get; } = new(false, null, null, [], [], [], []);
    /// <summary>Represents confirmation-required or stale confirmation.</summary>
    public static RegionLifecycleDeleteResult Conflict(EditorLifecycleConflictDto warning) => new(false, warning, null, [], [], [], []);
}
