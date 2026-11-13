using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

/// <summary>Shared direct graph construction; the caller owns authority and the outer transaction.</summary>
public sealed partial class TripCloneCoordinator
{
    /// <summary>Builds the complete detached clone and requires every semantic Place identity to map once.</summary>
    internal async Task<Trip> ConstructCloneAsync(
        Trip source,
        string destinationUserId,
        CancellationToken cancellationToken, bool keepPlanning = false,
        ICollection<TripCompositionMapping>? identities = null)
    {
        if (source.Regions.Count(r => r.Name == "Unassigned Places") > 1)
            throw new InvalidOperationException("Duplicate Unassigned Places Regions require reconciliation.");
        var own = source.UserId == destinationUserId;
        keepPlanning &= own;
        var clone = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = destinationUserId,
            Name = $"{source.Name} (Copy)",
            Notes = source.Notes,
            Credits = own ? TripCreditCodec.Read(TripCreditCodec.Write(source.Credits)) : TripCreditCodec.Inherited(source),
            IsPublic = false,
            ShareProgressEnabled = false,
            CenterLat = source.CenterLat,
            CenterLon = source.CenterLon,
            Zoom = source.Zoom,
            CoverImageUrl = source.CoverImageUrl,
            UpdatedAt = DateTime.UtcNow,
            Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(source.Planning, true) : null
        };
        identities?.Add(new("trip", source.Id, clone.Id));
        var placeMap = new Dictionary<Guid, Guid>();
        foreach (var sourceRegion in source.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id))
        {
            var region = CopyRegion(sourceRegion, clone.Id, destinationUserId, keepPlanning);
            region.DisplayOrder = clone.Regions.Count;
            identities?.Add(new("region", sourceRegion.Id, region.Id));
            foreach (var sourcePlace in sourceRegion.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id))
            {
                var place = CopyPlace(sourcePlace, region.Id, destinationUserId, keepPlanning);
                identities?.Add(new("place", sourcePlace.Id, place.Id));
                if (!placeMap.TryAdd(sourcePlace.Id, place.Id))
                    throw new InvalidOperationException("A source Place identity was loaded more than once.");
                place.DisplayOrder = region.Places.Count;
                region.Places.Add(place);
            }
            foreach (var sourceArea in sourceRegion.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id))
            {
                var area = CopyArea(sourceArea, region.Id, keepPlanning);
                identities?.Add(new("area", sourceArea.Id, area.Id));
                area.DisplayOrder = region.Areas.Count;
                region.Areas.Add(area);
            }
            clone.Regions.Add(region);
        }

        var sourcePlaces = source.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id);
        foreach (var sourceSegment in source.Segments.OrderBy(segment => segment.DisplayOrder).ThenBy(segment => segment.Id))
        {
            sourceSegment.FromPlace = sourceSegment.FromPlaceId is { } from ? sourcePlaces.GetValueOrDefault(from) : null;
            sourceSegment.ToPlace = sourceSegment.ToPlaceId is { } to ? sourcePlaces.GetValueOrDefault(to) : null;
            foreach (var waypoint in sourceSegment.Waypoints)
                waypoint.Place = sourcePlaces.GetValueOrDefault(waypoint.PlaceId)!;
            ValidateCopyRoute(sourceSegment);
            var segment = CreateSegmentClone(sourceSegment, clone.Id, destinationUserId, placeMap);
            segment.DisplayOrder = clone.Segments.Count;
            segment.Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(sourceSegment.Planning, true) : null;
            identities?.Add(new("segment", sourceSegment.Id, segment.Id));
            clone.Segments.Add(segment);
        }

        var tagIds = source.Tags.Select(tag => tag.Id).Distinct().ToArray();
        var tags = await dbContext.Tags.Where(tag => tagIds.Contains(tag.Id)).ToArrayAsync(cancellationToken);
        if (tags.Length != tagIds.Length)
            throw new InvalidOperationException("A source Tag identity could not be resolved.");
        foreach (var tag in tags) clone.Tags.Add(tag);
        return clone;
    }

    /// <summary>Constructs normal or canonical Region metadata under caller-owned destination authority.</summary>
    internal static Region CopyRegion(Region source, Guid tripId, string owner, bool keepPlanning) =>
        new Region
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            TripId = tripId,
            Name = source.Name,
            Notes = source.Notes,
            DisplayOrder = 0,
            Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(source.Planning, true) : null,
            CoverImageUrl = source.CoverImageUrl,
            Center = CopyPoint(source.Center)
        };

    /// <summary>Constructs a detached Place, preserving the coherent permitted enrichment tuple.</summary>
    internal static Place CopyPlace(Place source, Guid regionId, string owner, bool keepPlanning)
    {
        var enrichment = ResolvedFeatureMetadata.NormalizePersisted(
            source.ResolvedFeatureName, source.ResolvedFeatureType,
            source.AddressEnrichmentProvider, source.AddressEnrichmentStorageMode,
            source.AddressEnrichedAt);
        return new Place
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            RegionId = regionId,
            Name = source.Name,
            Location = CopyPoint(source.Location),
            Notes = source.Notes,
            DisplayOrder = 0,
            Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(source.Planning, true) : null,
            IconName = source.IconName,
            MarkerColor = source.MarkerColor,
            Address = source.Address,
            ResolvedFeatureName = enrichment.Name,
            ResolvedFeatureType = enrichment.Type,
            AddressEnrichmentProvider = enrichment.Provider,
            AddressEnrichmentStorageMode = enrichment.StorageMode,
            AddressEnrichedAt = enrichment.EnrichedAt
        };
    }

    /// <summary>Copies the complete polygon, including every hole, without sharing mutable coordinates.</summary>
    internal static Area CopyArea(Area source, Guid regionId, bool keepPlanning)
    {
        return new Area
        {
            Id = Guid.NewGuid(),
            RegionId = regionId,
            Name = source.Name,
            Notes = source.Notes,
            DisplayOrder = 0,
            Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(source.Planning, true) : null,
            FillHex = source.FillHex,
            Geometry = (Polygon)source.Geometry.Copy()
        };
    }

    /// <summary>Checks source-coordinate compatibility before installing trusted unchanged-route measurements.</summary>
    internal static void ValidateCopyRoute(Segment source)
    {
        var errors = SegmentRouteReconciler.ValidateProjectedAggregate(source);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (source.RouteGeometry is not { } route) return;
        foreach (var endpoint in new[] { (source.FromPlace, Index: 0), (source.ToPlace, Index: route.NumPoints - 1) })
        {
            if (endpoint.Item1 == null) continue;
            var point = endpoint.Item1.Location;
            var vertex = route.GetCoordinateN(endpoint.Index);
            if (point == null || Math.Abs(point.X - vertex.X) > 0.0000001 || Math.Abs(point.Y - vertex.Y) > 0.0000001)
                throw new InvalidOperationException("The source route does not match its saved endpoint. Repair the route explicitly before cloning.");
        }
    }

    /// <summary>Creates one remapped Segment aggregate without sharing mutable geometry with its source.</summary>
    internal static Segment CreateSegmentClone(
        Segment source,
        Guid cloneTripId,
        string destinationUserId,
        IReadOnlyDictionary<Guid, Guid> placeMap)
    {
        SegmentMeasurementState.Validate(source);
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = cloneTripId,
            UserId = destinationUserId,
            FromPlaceId = MapOptionalPlace(source.FromPlaceId, placeMap),
            ToPlaceId = MapOptionalPlace(source.ToPlaceId, placeMap),
            Title = source.Title,
            Mode = source.Mode,
            TransportProfileId = source.TransportProfileId,
            RouteGeometry = source.RouteGeometry == null ? null : (LineString)source.RouteGeometry.Copy(),
            EstimatedDuration = source.EstimatedDuration,
            EstimatedDurationSource = source.EstimatedDurationSource,
            EstimatedDistanceKm = source.EstimatedDistanceKm,
            DisplayOrder = source.DisplayOrder,
            Notes = source.Notes
        };
        SegmentRouteProvenance.Capture(source)?.ApplyTo(segment);
        foreach (var waypoint in source.Waypoints.OrderBy(item => item.Position))
        {
            segment.Waypoints.Add(new SegmentWaypoint
            {
                SegmentId = segment.Id,
                PlaceId = MapRequiredPlace(waypoint.PlaceId, placeMap),
                Position = waypoint.Position,
                RouteVertexIndex = waypoint.RouteVertexIndex
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
