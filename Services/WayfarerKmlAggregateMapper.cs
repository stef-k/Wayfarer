using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>Validates and maps detached native KML transport data into one owned Trip aggregate.</summary>
internal static class WayfarerKmlAggregateMapper
{
    /// <summary>Creates a complete entity graph, remapping every source identity when requested.</summary>
    internal static Trip Map(
        WayfarerKmlDocument source,
        string userId,
        IReadOnlyDictionary<string, TransportProfile> profiles,
        bool remapIdentities,
        Guid? targetTripId = null)
    {
        var tripId = targetTripId ?? (remapIdentities ? Guid.NewGuid() : source.TripId);
        var regionIds = MapIds(source.Regions.Select(region => region.Id), remapIdentities, "Region");
        var sourcePlaces = source.Regions.SelectMany(region => region.Places).ToArray();
        var placeIds = MapIds(sourcePlaces.Select(place => place.Id), remapIdentities, "Place");
        var segmentIds = MapIds(source.Segments.Select(segment => segment.Id), remapIdentities, "Segment");
        var trip = new Trip
        {
            Id = tripId,
            UserId = userId,
            Name = source.Name,
            Notes = source.Notes,
            CoverImageUrl = source.CoverImageUrl,
            CenterLat = source.CenterLat,
            CenterLon = source.CenterLon,
            Zoom = source.Zoom,
            IsPublic = false,
            UpdatedAt = DateTime.UtcNow
        };

        var places = new Dictionary<Guid, Place>();
        foreach (var sourceRegion in source.Regions)
        {
            var region = new Region
            {
                Id = regionIds[sourceRegion.Id], TripId = tripId, UserId = userId, Name = sourceRegion.Name,
                DisplayOrder = sourceRegion.DisplayOrder, Notes = sourceRegion.Notes, Center = Copy(sourceRegion.Center)
            };
            trip.Regions.Add(region);
            foreach (var sourcePlace in sourceRegion.Places)
            {
                var place = new Place
                {
                    Id = placeIds[sourcePlace.Id], RegionId = region.Id, Region = region, UserId = userId,
                    Name = sourcePlace.Name, DisplayOrder = sourcePlace.DisplayOrder, Notes = sourcePlace.Notes,
                    IconName = sourcePlace.IconName, MarkerColor = sourcePlace.MarkerColor, Address = sourcePlace.Address,
                    Location = Copy(sourcePlace.Location)
                };
                region.Places.Add(place);
                places[sourcePlace.Id] = place;
            }
            foreach (var sourceArea in sourceRegion.Areas)
                region.Areas.Add(new Area
                {
                    Id = remapIdentities ? Guid.NewGuid() : sourceArea.Id,
                    RegionId = region.Id,
                    Name = sourceArea.Name,
                    DisplayOrder = sourceArea.DisplayOrder,
                    Notes = sourceArea.Notes,
                    FillHex = sourceArea.FillHex,
                    Geometry = Copy(sourceArea.Geometry) ?? throw new TripImportValidationException("Area geometry is required.")
                });
        }
        if (source.Version == 1 && !trip.Regions.Any(region => region.Name == "Unassigned Places"))
        {
            foreach (var region in trip.Regions) region.DisplayOrder++;
            trip.Regions.Add(new Region
            {
                Id = Guid.NewGuid(), TripId = tripId, UserId = userId,
                Name = "Unassigned Places", DisplayOrder = 0
            });
        }

        foreach (var sourceSegment in source.Segments)
        {
            var fromId = MapReference(sourceSegment.FromPlaceId, placeIds, "From");
            var toId = MapReference(sourceSegment.ToPlaceId, placeIds, "To");
            var profile = ResolveProfile(source.Version, sourceSegment, profiles);
            ValidateDuration(sourceSegment, profile);
            var segment = new Segment
            {
                Id = segmentIds[sourceSegment.Id], TripId = tripId, UserId = userId,
                FromPlaceId = fromId, FromPlace = sourceSegment.FromPlaceId.HasValue ? places[sourceSegment.FromPlaceId.Value] : null,
                ToPlaceId = toId, ToPlace = sourceSegment.ToPlaceId.HasValue ? places[sourceSegment.ToPlaceId.Value] : null,
                Mode = sourceSegment.Mode, TransportProfileId = profile?.Id,
                RouteGeometry = sourceSegment.HasCustomRoute ? Copy(sourceSegment.Geometry) : null,
                EstimatedDistanceKm = null,
                EstimatedDuration = sourceSegment.DurationSource == EstimatedDurationSource.Manual && sourceSegment.DurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(sourceSegment.DurationSeconds.Value) : null,
                EstimatedDurationSource = sourceSegment.DurationSource,
                DisplayOrder = sourceSegment.DisplayOrder,
                Notes = sourceSegment.Notes
            };
            for (var position = 0; position < sourceSegment.WaypointPlaceIds.Count; position++)
            {
                var sourcePlaceId = sourceSegment.WaypointPlaceIds[position];
                if (!placeIds.TryGetValue(sourcePlaceId, out var placeId)) throw new TripImportValidationException("A waypoint Place could not be mapped.");
                segment.Waypoints.Add(new SegmentWaypoint
                {
                    SegmentId = segment.Id, Segment = segment, PlaceId = placeId, Place = places[sourcePlaceId],
                    Position = position, RouteVertexIndex = sourceSegment.WaypointRouteVertexIndices[position]
                });
            }
            ValidateCompatibilityGeometry(sourceSegment, segment);
            var errors = SegmentRouteReconciler.ValidateProjectedAggregate(segment);
            if (errors.Count > 0) throw new TripImportValidationException(string.Join(" ", errors));
            trip.Segments.Add(segment);
        }
        return trip;
    }

    private static Dictionary<Guid, Guid> MapIds(IEnumerable<Guid> ids, bool remap, string label)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var id in ids)
            if (id == Guid.Empty || !result.TryAdd(id, remap ? Guid.NewGuid() : id))
                throw new TripImportValidationException($"Duplicate or empty {label} identity.");
        return result;
    }

    private static Guid? MapReference(Guid? sourceId, IReadOnlyDictionary<Guid, Guid> map, string label)
    {
        if (!sourceId.HasValue) return null;
        return map.TryGetValue(sourceId.Value, out var targetId) ? targetId
            : throw new TripImportValidationException($"The {label} Place could not be mapped.");
    }

    private static TransportProfile? ResolveProfile(
        int version, WayfarerKmlSegment segment, IReadOnlyDictionary<string, TransportProfile> profiles)
    {
        var key = version == 1 ? TransportProfile.NormalizeKey(segment.Mode) : segment.TransportProfileKey;
        if (key.Length == 0) return null;
        if (key != key.Trim() || key != TransportProfile.NormalizeKey(key) || !profiles.TryGetValue(key, out var profile))
            throw new TripImportValidationException("The transport profile key is unknown.");
        if (TransportProfile.NormalizeKey(segment.Mode) != key)
            throw new TripImportValidationException("Segment mode and transport profile key do not match.");
        return profile;
    }

    private static void ValidateDuration(WayfarerKmlSegment segment, TransportProfile? profile)
    {
        if (segment.DurationSource == EstimatedDurationSource.Manual && !segment.DurationSeconds.HasValue)
            throw new TripImportValidationException("Manual duration requires exact whole seconds.");
        if (segment.DurationSeconds < 0 || segment.DurationSeconds > TimeSpan.MaxValue.TotalSeconds)
            throw new TripImportValidationException("Manual duration is outside the supported range.");
        if (segment.DurationSource == EstimatedDurationSource.Automatic && profile is null && segment.DurationSeconds.HasValue)
            throw new TripImportValidationException("Unavailable Automatic duration metadata must be empty.");
    }

    private static void ValidateCompatibilityGeometry(WayfarerKmlSegment source, Segment segment)
    {
        if (source.HasCustomRoute)
        {
            if (source.Geometry is null || source.WaypointRouteVertexIndices.Any(index => !index.HasValue))
                throw new TripImportValidationException("Custom route metadata is incomplete.");
            return;
        }
        if (source.WaypointRouteVertexIndices.Any(index => index.HasValue))
            throw new TripImportValidationException("Fallback route indices must be null.");
        if (source.Geometry is null) return;
        var anchors = new[] { segment.FromPlace }.Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place)).Append(segment.ToPlace).ToArray();
        if (anchors.Any(place => place?.Location is null) || source.Geometry.NumPoints != anchors.Length)
            throw new TripImportValidationException("Fallback compatibility geometry does not match its anchor chain.");
        for (var index = 0; index < anchors.Length; index++)
        {
            var actual = source.Geometry.GetCoordinateN(index);
            var expected = anchors[index]!.Location!.Coordinate;
            if (Math.Abs(actual.X - expected.X) > 0.0000001 || Math.Abs(actual.Y - expected.Y) > 0.0000001)
                throw new TripImportValidationException("Fallback compatibility geometry does not match its anchor chain.");
        }
    }

    private static Point? Copy(Point? point) => point is null ? null : new Point(point.X, point.Y) { SRID = point.SRID };
    private static LineString? Copy(LineString? line) => line is null ? null : new LineString(line.Coordinates.Select(c => new Coordinate(c.X, c.Y)).ToArray()) { SRID = line.SRID };
    private static Polygon? Copy(Polygon? polygon) => polygon is null ? null : (Polygon)polygon.Copy();
}
