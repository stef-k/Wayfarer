using System.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Describes an eligibility or success outcome from one authoritative Trip clone attempt.</summary>
public sealed record TripCloneResult(
    TripCloneStatus Status,
    Guid? ClonedTripId = null,
    string? SourceTripName = null,
    bool RequiresImageWarmup = false);

/// <summary>Identifies path-neutral Trip clone outcomes translated by each controller.</summary>
public enum TripCloneStatus
{
    /// <summary>The source Trip does not exist.</summary>
    NotFound,
    /// <summary>The source Trip is not public.</summary>
    NotPublic,
    /// <summary>The destination user already owns the source Trip.</summary>
    AlreadyOwned,
    /// <summary>The complete private clone committed successfully.</summary>
    Succeeded
}

/// <summary>Loads, validates, constructs, reconciles, and atomically persists complete Trip clones.</summary>
public sealed class TripCloneCoordinator(ApplicationDbContext dbContext)
{
    /// <summary>Clones one eligible public Trip into the destination user's account.</summary>
    public async Task<TripCloneResult> CloneAsync(
        Guid sourceTripId,
        string destinationUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        try
        {
            var source = await LoadSourceAsync(sourceTripId, cancellationToken);
            if (source == null) return new(TripCloneStatus.NotFound);
            if (!source.IsPublic) return new(TripCloneStatus.NotPublic);
            if (source.UserId == destinationUserId) return new(TripCloneStatus.AlreadyOwned);

            var clone = await ConstructCloneAsync(source, destinationUserId, cancellationToken);
            dbContext.Trips.Add(clone);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            await SegmentMeasurementWriterReconciler.ReconcileTripAsync(
                dbContext, clone.Id, allowUnavailableAutomatic: true, cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);

            var requiresWarmup = !string.IsNullOrWhiteSpace(clone.CoverImageUrl)
                || HtmlHelpers.ExtractExternalImageUrls(clone.Notes).Any();
            return new(TripCloneStatus.Succeeded, clone.Id, source.Name, requiresWarmup);
        }
        catch (Exception cloneFailure) when (transaction != null)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Trip cloning failed and its transaction rollback also failed.",
                    cloneFailure, rollbackFailure);
            }
            throw;
        }
    }

    /// <summary>Explicitly loads every clone-owned aggregate child inside the coordinator snapshot.</summary>
    private Task<Trip?> LoadSourceAsync(Guid sourceTripId, CancellationToken cancellationToken) =>
        dbContext.Trips.AsNoTracking()
            .Include(trip => trip.Tags)
            .Include(trip => trip.Regions).ThenInclude(region => region.Places)
            .Include(trip => trip.Regions).ThenInclude(region => region.Areas)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.Waypoints)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.TransportProfile)
            .AsSplitQuery()
            .SingleOrDefaultAsync(trip => trip.Id == sourceTripId, cancellationToken);

    /// <summary>Builds the complete detached clone and requires every semantic Place identity to map once.</summary>
    private async Task<Trip> ConstructCloneAsync(
        Trip source,
        string destinationUserId,
        CancellationToken cancellationToken)
    {
        var clone = new Trip
        {
            Id = Guid.NewGuid(), UserId = destinationUserId, Name = $"{source.Name} (Copy)",
            Notes = source.Notes, IsPublic = false, ShareProgressEnabled = false,
            CenterLat = source.CenterLat, CenterLon = source.CenterLon, Zoom = source.Zoom,
            CoverImageUrl = source.CoverImageUrl, UpdatedAt = DateTime.UtcNow
        };
        var placeMap = new Dictionary<Guid, Guid>();
        foreach (var sourceRegion in source.Regions)
        {
            var region = new Region
            {
                Id = Guid.NewGuid(), UserId = destinationUserId, TripId = clone.Id,
                Name = sourceRegion.Name, Notes = sourceRegion.Notes,
                DisplayOrder = sourceRegion.DisplayOrder, CoverImageUrl = sourceRegion.CoverImageUrl,
                Center = CopyPoint(sourceRegion.Center)
            };
            foreach (var sourcePlace in sourceRegion.Places)
            {
                var placeId = Guid.NewGuid();
                if (!placeMap.TryAdd(sourcePlace.Id, placeId))
                    throw new InvalidOperationException("A source Place identity was loaded more than once.");
                region.Places.Add(new Place
                {
                    Id = placeId, UserId = destinationUserId, RegionId = region.Id,
                    Name = sourcePlace.Name, Location = CopyPoint(sourcePlace.Location), Notes = sourcePlace.Notes,
                    DisplayOrder = sourcePlace.DisplayOrder, IconName = sourcePlace.IconName,
                    MarkerColor = sourcePlace.MarkerColor, Address = sourcePlace.Address
                });
            }
            foreach (var sourceArea in sourceRegion.Areas)
            {
                region.Areas.Add(new Area
                {
                    Id = Guid.NewGuid(), RegionId = region.Id, Name = sourceArea.Name,
                    Notes = sourceArea.Notes, DisplayOrder = sourceArea.DisplayOrder,
                    FillHex = sourceArea.FillHex, Geometry = (Polygon)sourceArea.Geometry.Copy()
                });
            }
            clone.Regions.Add(region);
        }

        foreach (var sourceSegment in source.Segments.OrderBy(segment => segment.DisplayOrder).ThenBy(segment => segment.Id))
            clone.Segments.Add(CreateSegmentClone(sourceSegment, clone.Id, destinationUserId, placeMap));

        var tagIds = source.Tags.Select(tag => tag.Id).Distinct().ToArray();
        var tags = await dbContext.Tags.Where(tag => tagIds.Contains(tag.Id)).ToArrayAsync(cancellationToken);
        if (tags.Length != tagIds.Length)
            throw new InvalidOperationException("A source Tag identity could not be resolved.");
        foreach (var tag in tags) clone.Tags.Add(tag);
        return clone;
    }

    /// <summary>Creates one remapped Segment aggregate without sharing mutable geometry with its source.</summary>
    private static Segment CreateSegmentClone(
        Segment source,
        Guid cloneTripId,
        string destinationUserId,
        IReadOnlyDictionary<Guid, Guid> placeMap)
    {
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = cloneTripId, UserId = destinationUserId,
            FromPlaceId = MapOptionalPlace(source.FromPlaceId, placeMap),
            ToPlaceId = MapOptionalPlace(source.ToPlaceId, placeMap),
            Mode = source.Mode, TransportProfileId = source.TransportProfileId,
            RouteGeometry = source.RouteGeometry == null ? null : (LineString)source.RouteGeometry.Copy(),
            EstimatedDuration = source.EstimatedDuration,
            EstimatedDurationSource = source.EstimatedDurationSource,
            EstimatedDistanceKm = null, DisplayOrder = source.DisplayOrder, Notes = source.Notes
        };
        foreach (var waypoint in source.Waypoints.OrderBy(item => item.Position))
        {
            segment.Waypoints.Add(new SegmentWaypoint
            {
                SegmentId = segment.Id, PlaceId = MapRequiredPlace(waypoint.PlaceId, placeMap),
                Position = waypoint.Position, RouteVertexIndex = waypoint.RouteVertexIndex
            });
        }
        return segment;
    }

    /// <summary>Maps an optional endpoint without converting an invalid source reference to null.</summary>
    private static Guid? MapOptionalPlace(Guid? sourcePlaceId, IReadOnlyDictionary<Guid, Guid> placeMap) =>
        sourcePlaceId.HasValue ? MapRequiredPlace(sourcePlaceId.Value, placeMap) : null;

    /// <summary>Requires one deterministic cloned identity for a referenced source Place.</summary>
    private static Guid MapRequiredPlace(Guid sourcePlaceId, IReadOnlyDictionary<Guid, Guid> placeMap) =>
        placeMap.TryGetValue(sourcePlaceId, out var clonedPlaceId)
            ? clonedPlaceId
            : throw new InvalidOperationException("A Segment references a Place outside the source Trip.");

    /// <summary>Copies a mutable Point geometry using value semantics.</summary>
    private static Point? CopyPoint(Point? point) => point == null ? null : (Point)point.Copy();
}
