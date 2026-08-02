using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Runtime.ExceptionServices;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Describes one proposed intermediate saved-place anchor by canonical identity.</summary>
/// <param name="PlaceId">Canonical saved-place identity.</param>
/// <param name="Position">Zero-based position in the submitted sequence.</param>
/// <param name="RouteVertexIndex">Custom-route vertex index, or null for fallback geometry.</param>
public sealed record SegmentWaypointProposal(Guid PlaceId, int Position, int? RouteVertexIndex);

/// <summary>Describes a complete persisted Segment route aggregate proposal.</summary>
/// <param name="SegmentId">Canonical Segment identity.</param>
/// <param name="FromPlaceId">Proposed canonical origin identity.</param>
/// <param name="ToPlaceId">Proposed canonical destination identity.</param>
/// <param name="Waypoints">Ordered waypoint scalar proposals.</param>
/// <param name="RouteGeometry">Proposed custom route, or null for fallback rendering.</param>
public sealed record SegmentRouteProposal(
    Guid SegmentId,
    Guid? FromPlaceId,
    Guid? ToPlaceId,
    IReadOnlyList<SegmentWaypointProposal> Waypoints,
    LineString? RouteGeometry);

/// <summary>Reports whether a route proposal committed and its effective canonical anchor chain.</summary>
/// <param name="Succeeded">Whether validation succeeded and the aggregate committed.</param>
/// <param name="Errors">Deterministic aggregate validation errors.</param>
/// <param name="EffectiveAnchorChain">Canonical saved-place anchors used by fallback rendering.</param>
public sealed record SegmentRouteReconciliationResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<Place> EffectiveAnchorChain);

/// <summary>Loads, validates, and atomically persists canonical Segment route aggregate state.</summary>
public static class SegmentRouteReconciler
{
    /// <summary>Maximum independent longitude or latitude difference accepted for an anchor vertex.</summary>
    public const double CoordinateToleranceDegrees = 0.0000001d;

    /// <summary>
    /// Requires a clean caller context and owns the transaction, canonical Segment row lock, and SaveChanges
    /// boundary required to replace ordered waypoint rows as one serialized aggregate proposal.
    /// </summary>
    public static async Task<SegmentRouteReconciliationResult> ReconcileAsync(
        ApplicationDbContext dbContext,
        SegmentRouteProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(proposal.Waypoints);
        EnsureCleanContext(dbContext);
        if (dbContext.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Segment route reconciliation owns its transaction boundary.");

        if (!dbContext.Database.IsRelational())
        {
            var segment = await LoadAggregateAsync(dbContext, proposal.SegmentId, cancellationToken);
            if (segment == null) return new(false, ["Segment was not found."], []);
            var placesById = await LoadProposalPlacesAsync(dbContext, proposal, cancellationToken);
            var geometry = CopyGeometry(proposal);
            var errors = Validate(segment.TripId, proposal, placesById, geometry);
            var anchors = BuildAnchorChain(proposal, placesById);
            if (errors.Count > 0) return new(false, errors, anchors);
            ApplyTrackedState(dbContext, segment, proposal, placesById, geometry);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(true, [], anchors);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockSegmentAsync(dbContext, proposal.SegmentId, cancellationToken);

            await RefreshStaleCanonicalStateAsync(dbContext, proposal.SegmentId, cancellationToken);
            var segment = await LoadAggregateAsync(dbContext, proposal.SegmentId, cancellationToken);
            if (segment == null) return new(false, ["Segment was not found."], []);
            var placesById = await LoadProposalPlacesAsync(dbContext, proposal, cancellationToken);
            var geometry = CopyGeometry(proposal);
            var errors = Validate(segment.TripId, proposal, placesById, geometry);
            var anchors = BuildAnchorChain(proposal, placesById);
            if (errors.Count > 0) return new(false, errors, anchors);

            await dbContext.Set<SegmentWaypoint>()
                .Where(item => item.SegmentId == segment.Id)
                .ExecuteDeleteAsync(cancellationToken);
            DetachCurrentWaypoints(dbContext, segment);
            ApplyTrackedState(dbContext, segment, proposal, placesById, geometry);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, [], anchors);
        }
        catch (Exception originalFailure)
        {
            var cleanupFailures = new List<Exception>();
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure) { cleanupFailures.Add(rollbackFailure); }

            try { await RecoverAggregateAsync(dbContext, proposal.SegmentId, CancellationToken.None); }
            catch (Exception recoveryFailure) { cleanupFailures.Add(recoveryFailure); }

            if (cleanupFailures.Count > 0)
            {
                try { await dbContext.DisposeAsync(); }
                catch (Exception disposalFailure) { cleanupFailures.Add(disposalFailure); }
                throw new AggregateException(
                    "Segment route reconciliation failed and mandatory cleanup could not restore a reusable DbContext.",
                    [originalFailure, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }
    }

    /// <summary>Rejects pending caller work because this operation owns SaveChanges and recovery.</summary>
    private static void EnsureCleanContext(ApplicationDbContext dbContext)
    {
        dbContext.ChangeTracker.DetectChanges();
        if (dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Segment route reconciliation requires a clean DbContext.");
    }

    /// <summary>Locks only the canonical Segment row for the lifetime of the owned PostgreSQL transaction.</summary>
    private static async Task LockSegmentAsync(
        ApplicationDbContext dbContext,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {segmentId} FOR UPDATE",
            cancellationToken);
    }

    /// <summary>Refreshes unchanged identity-map values which could predate acquisition of the row lock.</summary>
    private static async Task RefreshStaleCanonicalStateAsync(
        ApplicationDbContext dbContext,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var trackedSegment = dbContext.ChangeTracker.Entries<Segment>()
            .SingleOrDefault(entry => entry.Entity.Id == segmentId);
        foreach (var entry in dbContext.ChangeTracker.Entries<SegmentWaypoint>()
                     .Where(entry => entry.Entity.SegmentId == segmentId).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in dbContext.ChangeTracker.Entries<Place>().ToArray()) entry.State = EntityState.Detached;
        foreach (var entry in dbContext.ChangeTracker.Entries<Region>().ToArray()) entry.State = EntityState.Detached;
        if (trackedSegment == null) return;
        if (trackedSegment.State == EntityState.Detached)
        {
            var replacement = dbContext.ChangeTracker.Entries<Segment>()
                .SingleOrDefault(entry => entry.Entity.Id == segmentId);
            if (replacement != null) trackedSegment = replacement;
            else
            {
                trackedSegment.Entity.FromPlace = null;
                trackedSegment.Entity.ToPlace = null;
                trackedSegment.Entity.Waypoints = [];
                trackedSegment = dbContext.Attach(trackedSegment.Entity);
            }
        }
        trackedSegment.Entity.FromPlace = null;
        trackedSegment.Entity.ToPlace = null;
        trackedSegment.Entity.Waypoints = [];
        var canonical = await dbContext.Segments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == segmentId, cancellationToken);
        if (canonical == null) return;
        trackedSegment.CurrentValues.SetValues(canonical);
        trackedSegment.OriginalValues.SetValues(canonical);
        trackedSegment.State = EntityState.Unchanged;
    }

    private static LineString? CopyGeometry(SegmentRouteProposal proposal) =>
        proposal.RouteGeometry == null ? null : (LineString)proposal.RouteGeometry.Copy();

    private static async Task<Dictionary<Guid, Place>> LoadProposalPlacesAsync(
        ApplicationDbContext dbContext,
        SegmentRouteProposal proposal,
        CancellationToken cancellationToken)
    {
        var requiredPlaceIds = proposal.Waypoints.Select(item => (Guid?)item.PlaceId)
            .Append(proposal.FromPlaceId).Append(proposal.ToPlaceId)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        return await dbContext.Places.Include(place => place.Region)
            .Where(place => requiredPlaceIds.Contains(place.Id))
            .ToDictionaryAsync(place => place.Id, cancellationToken);
    }

    /// <summary>Loads the complete tracked aggregate needed for mutation and failure recovery.</summary>
    internal static Task<Segment?> LoadAggregateAsync(
        ApplicationDbContext dbContext,
        Guid segmentId,
        CancellationToken cancellationToken = default) =>
        dbContext.Segments
            .Include(segment => segment.FromPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.ToPlace).ThenInclude(place => place!.Region)
            .Include(segment => segment.Waypoints.OrderBy(waypoint => waypoint.Position))
                .ThenInclude(waypoint => waypoint.Place).ThenInclude(place => place.Region)
            .SingleOrDefaultAsync(segment => segment.Id == segmentId, cancellationToken);

    private static void ApplyTrackedState(
        ApplicationDbContext dbContext,
        Segment segment,
        SegmentRouteProposal proposal,
        IReadOnlyDictionary<Guid, Place> placesById,
        LineString? geometry)
    {
        if (!dbContext.Database.IsRelational())
        {
            dbContext.RemoveRange(segment.Waypoints);
            segment.Waypoints.Clear();
        }

        segment.FromPlaceId = proposal.FromPlaceId;
        segment.FromPlace = proposal.FromPlaceId.HasValue ? placesById[proposal.FromPlaceId.Value] : null;
        segment.ToPlaceId = proposal.ToPlaceId;
        segment.ToPlace = proposal.ToPlaceId.HasValue ? placesById[proposal.ToPlaceId.Value] : null;
        segment.RouteGeometry = geometry;
        foreach (var proposed in proposal.Waypoints)
        {
            segment.Waypoints.Add(new SegmentWaypoint
            {
                SegmentId = segment.Id,
                Segment = segment,
                PlaceId = proposed.PlaceId,
                Place = placesById[proposed.PlaceId],
                Position = proposed.Position,
                RouteVertexIndex = proposed.RouteVertexIndex
            });
        }
    }

    private static void DetachCurrentWaypoints(ApplicationDbContext dbContext, Segment segment)
    {
        foreach (var waypoint in segment.Waypoints.ToArray())
            dbContext.Entry(waypoint).State = EntityState.Detached;
        segment.Waypoints = new List<SegmentWaypoint>();
    }

    private static async Task RecoverAggregateAsync(
        ApplicationDbContext dbContext,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var segment = dbContext.ChangeTracker.Entries<Segment>()
            .SingleOrDefault(entry => entry.Entity.Id == segmentId)?.Entity;
        foreach (var entry in dbContext.ChangeTracker.Entries<SegmentWaypoint>()
                     .Where(entry => entry.Entity.SegmentId == segmentId).ToArray())
            entry.State = EntityState.Detached;
        if (segment == null)
        {
            await LoadAggregateAsync(dbContext, segmentId, cancellationToken);
            return;
        }
        var canonical = await dbContext.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == segmentId, cancellationToken);
        var segmentEntry = dbContext.Entry(segment);
        segmentEntry.CurrentValues.SetValues(canonical);
        segmentEntry.OriginalValues.SetValues(canonical);
        segmentEntry.State = EntityState.Unchanged;
        var endpointIds = new[] { segment.FromPlaceId, segment.ToPlaceId }
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var endpoints = await dbContext.Places.Include(item => item.Region)
            .Where(item => endpointIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        segment.FromPlace = segment.FromPlaceId.HasValue ? endpoints[segment.FromPlaceId.Value] : null;
        segment.ToPlace = segment.ToPlaceId.HasValue ? endpoints[segment.ToPlaceId.Value] : null;
        segment.Waypoints = await dbContext.Set<SegmentWaypoint>()
            .Where(item => item.SegmentId == segment.Id)
            .Include(item => item.Place).ThenInclude(place => place.Region)
            .OrderBy(item => item.Position)
            .ToListAsync(cancellationToken);
        var trackedTrip = dbContext.ChangeTracker.Entries<Trip>()
            .SingleOrDefault(entry => entry.Entity.Id == segment.TripId);
        if (trackedTrip is { State: not EntityState.Unchanged })
            await trackedTrip.ReloadAsync(cancellationToken);
    }

    private static List<string> Validate(
        Guid tripId,
        SegmentRouteProposal proposal,
        IReadOnlyDictionary<Guid, Place> placesById,
        LineString? geometry)
    {
        var errors = new List<string>();
        if (proposal.Waypoints.Count > 0)
        {
            ValidateIdentity("From place", proposal.FromPlaceId, placesById, errors);
            ValidateIdentity("To place", proposal.ToPlaceId, placesById, errors);
        }
        else
        {
            ValidateOptionalIdentity("From place", proposal.FromPlaceId, placesById, errors);
            ValidateOptionalIdentity("To place", proposal.ToPlaceId, placesById, errors);
        }
        for (var index = 0; index < proposal.Waypoints.Count; index++)
        {
            if (!placesById.ContainsKey(proposal.Waypoints[index].PlaceId))
                errors.Add($"Waypoint place at position {index} was not found.");
        }
        if (errors.Count > 0) return errors;
        if (proposal.Waypoints.Count == 0) return errors;

        var from = placesById[proposal.FromPlaceId!.Value];
        var to = placesById[proposal.ToPlaceId!.Value];
        ValidateCanonicalPlace("From place", from, tripId, errors);
        ValidateCanonicalPlace("To place", to, tripId, errors);
        var placeIds = new HashSet<Guid>();
        for (var index = 0; index < proposal.Waypoints.Count; index++)
        {
            var waypoint = proposal.Waypoints[index];
            var place = placesById[waypoint.PlaceId];
            if (waypoint.Position != index)
                errors.Add("Waypoint positions must be unique and contiguous from zero in submitted order.");
            if (!placeIds.Add(waypoint.PlaceId))
                errors.Add("Intermediate waypoint places must be unique within a segment.");
            if (waypoint.PlaceId == proposal.FromPlaceId) errors.Add("A waypoint cannot equal the From place.");
            if (waypoint.PlaceId == proposal.ToPlaceId) errors.Add("A waypoint cannot equal the To place.");
            ValidateCanonicalPlace("Every waypoint place", place, tripId, errors);
        }

        if (geometry == null)
        {
            if (proposal.Waypoints.Any(waypoint => waypoint.RouteVertexIndex.HasValue))
                errors.Add("Fallback geometry requires null waypoint route vertex indices.");
            return errors;
        }

        ValidateCustomGeometry(from, to, proposal.Waypoints, placesById, geometry, errors);
        return errors;
    }

    private static void ValidateIdentity(
        string label,
        Guid? id,
        IReadOnlyDictionary<Guid, Place> placesById,
        List<string> errors)
    {
        if (!id.HasValue) errors.Add($"{label} is required when a segment has waypoints.");
        else if (!placesById.ContainsKey(id.Value)) errors.Add($"{label} was not found.");
    }

    private static void ValidateOptionalIdentity(
        string label,
        Guid? id,
        IReadOnlyDictionary<Guid, Place> placesById,
        List<string> errors)
    {
        if (id.HasValue && !placesById.ContainsKey(id.Value)) errors.Add($"{label} was not found.");
    }

    private static void ValidateCanonicalPlace(string label, Place place, Guid tripId, List<string> errors)
    {
        if (place.Region?.TripId != tripId) errors.Add($"{label} must belong to the segment trip.");
        if (!HasValidLocation(place)) errors.Add($"{label} must have a valid SRID 4326 location.");
    }

    private static void ValidateCustomGeometry(
        Place from,
        Place to,
        IReadOnlyList<SegmentWaypointProposal> waypoints,
        IReadOnlyDictionary<Guid, Place> placesById,
        LineString geometry,
        List<string> errors)
    {
        if (geometry.SRID != 4326 || geometry.IsEmpty || geometry.NumPoints < 2 || !geometry.IsValid)
        {
            errors.Add("Custom route geometry must be a valid SRID 4326 LineString with at least two vertices.");
            return;
        }
        if (!CoordinatesMatch(geometry.GetCoordinateN(0), from.Location!.Coordinate))
            errors.Add("The first custom-route vertex must match the From place.");
        if (!CoordinatesMatch(geometry.GetCoordinateN(geometry.NumPoints - 1), to.Location!.Coordinate))
            errors.Add("The last custom-route vertex must match the To place.");

        var priorIndex = 0;
        var usedIndices = new HashSet<int>();
        foreach (var waypoint in waypoints)
        {
            if (!waypoint.RouteVertexIndex.HasValue)
            {
                errors.Add("Every waypoint requires a route vertex index for custom geometry.");
                continue;
            }
            var vertexIndex = waypoint.RouteVertexIndex.Value;
            if (!usedIndices.Add(vertexIndex)) errors.Add("Waypoint route vertex indices must be unique.");
            if (vertexIndex <= priorIndex) errors.Add("Waypoint route vertex indices must increase in waypoint order.");
            if (vertexIndex <= 0 || vertexIndex >= geometry.NumPoints - 1)
                errors.Add("Waypoint route vertex indices must identify interior route vertices.");
            else if (!CoordinatesMatch(geometry.GetCoordinateN(vertexIndex), placesById[waypoint.PlaceId].Location!.Coordinate))
                errors.Add("Each indexed custom-route vertex must match its waypoint place.");
            priorIndex = vertexIndex;
        }
    }

    private static IReadOnlyList<Place> BuildAnchorChain(
        SegmentRouteProposal proposal,
        IReadOnlyDictionary<Guid, Place> placesById)
    {
        var anchors = new List<Place>(proposal.Waypoints.Count + 2);
        if (proposal.FromPlaceId.HasValue && placesById.TryGetValue(proposal.FromPlaceId.Value, out var from)) anchors.Add(from);
        foreach (var waypoint in proposal.Waypoints)
            if (placesById.TryGetValue(waypoint.PlaceId, out var place)) anchors.Add(place);
        if (proposal.ToPlaceId.HasValue && placesById.TryGetValue(proposal.ToPlaceId.Value, out var to)) anchors.Add(to);
        return anchors;
    }

    private static bool HasValidLocation(Place place) =>
        place.Location is { IsEmpty: false, SRID: 4326 } location
        && double.IsFinite(location.X)
        && double.IsFinite(location.Y);

    private static bool CoordinatesMatch(Coordinate actual, Coordinate expected)
    {
        if (!double.IsFinite(actual.X) || !double.IsFinite(actual.Y)
            || !double.IsFinite(expected.X) || !double.IsFinite(expected.Y)) return false;
        const decimal tolerance = 0.0000001m;
        return Math.Abs((decimal)actual.X - (decimal)expected.X) <= tolerance
            && Math.Abs((decimal)actual.Y - (decimal)expected.Y) <= tolerance;
    }
}
