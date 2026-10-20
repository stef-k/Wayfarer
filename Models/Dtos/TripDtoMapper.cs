using Wayfarer.Util;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Models.Dtos;

/// <summary>Projects public Trip content; full reads additionally require authoritative complete state.</summary>
public static class TripDtoMapper
{
    private static readonly GeoJsonWriter _geoJsonWriter = new();

    /// <summary>Validates and eagerly projects the entire loaded, authorized graph without repairing storage.</summary>
    public static ApiTripDto ToFullApiDto(this Trip trip, ApplicationDbContext db, CancellationToken token = default)
    {
        Require(trip.Name is not null && IsFinite(trip.CenterLat) && IsFinite(trip.CenterLon));
        Require(db.Entry(trip).Collection(item => item.Regions).IsLoaded && trip.Regions is not null);
        Require(db.Entry(trip).Collection(item => item.Segments).IsLoaded && trip.Segments is not null);
        Require(db.Entry(trip).Collection(item => item.Tags).IsLoaded && trip.Tags is not null);
        var placeIds = new HashSet<Guid>();
        foreach (var region in trip.Regions!)
        {
            token.ThrowIfCancellationRequested();
            Require(region is not null && region.TripId == trip.Id && region.Name is not null);
            Require(db.Entry(region!).Collection(item => item.Places).IsLoaded && region!.Places is not null);
            Require(db.Entry(region).Collection(item => item.Areas).IsLoaded && region.Areas is not null);
            ValidatePoint(region.Center);
            foreach (var place in region.Places!)
            {
                Require(place is not null && place.RegionId == region.Id && place.Name is not null);
                ValidatePoint(place!.Location);
                Require(placeIds.Add(place.Id));
            }
            foreach (var area in region.Areas!)
            {
                token.ThrowIfCancellationRequested();
                Require(area is not null && area.RegionId == region.Id && area.Name is not null);
                Require(area!.Geometry is { IsEmpty: false, SRID: 4326 } polygon && polygon.IsValid
                    && polygon.Coordinates.All(IsValidCoordinate));
            }
        }
        Require(trip.Tags!.All(tag => tag is not null && tag.Name is not null && tag.Slug is not null));
        Require(trip.Segments!.All(segment => segment is not null && segment.Waypoints is not null
            && segment.Waypoints.All(waypoint => waypoint is not null)));
        var segments = new List<ApiTripSegmentDto>();
        foreach (var segment in trip.Segments!.OrderBy(item => item.DisplayOrder))
        {
            token.ThrowIfCancellationRequested();
            Require(segment is not null && IsFinite(segment.EstimatedDistanceKm));
            var resolution = PublicSegmentResolver.Resolve(segment!, trip.Id, db);
            Require(resolution.Succeeded);
            var projected = resolution.Segment!;
            Require((!projected.FromPlaceId.HasValue || placeIds.Contains(projected.FromPlaceId.Value))
                && (!projected.ToPlaceId.HasValue || placeIds.Contains(projected.ToPlaceId.Value))
                && projected.Waypoints.All(waypoint => placeIds.Contains(waypoint.PlaceId)));
            segments.Add(projected);
        }
        token.ThrowIfCancellationRequested();
        var dto = trip.ToApiDto(segments);
        // The shared mutation mapper remains permissive; full reads must never accept its reduced Area result.
        foreach (var region in dto.Regions!)
        {
            token.ThrowIfCancellationRequested();
            foreach (var area in region.Areas!)
            {
                Require(!string.IsNullOrWhiteSpace(area.GeometryGeoJson));
                if (string.IsNullOrWhiteSpace(area.FillHex)) area.FillHex = "#3388FF";
            }
        }
        return dto;
    }

    /// <summary>Distinguishes legitimate absent points from unusable stored geography.</summary>
    private static void ValidatePoint(Point? point) =>
        Require(point is null || !point.IsEmpty && point.SRID == 4326 && IsValidCoordinate(point.Coordinate));

    /// <summary>Requires finite longitude/latitude in the same WGS84 bounds used by Segment projection.</summary>
    private static bool IsValidCoordinate(Coordinate coordinate) =>
        double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y)
        && coordinate.X is >= -180 and <= 180 && coordinate.Y is >= -90 and <= 90;

    /// <summary>Allows independent null numeric metadata without accepting named floating-point strings.</summary>
    private static bool IsFinite(double? value) => !value.HasValue || double.IsFinite(value.Value);

    /// <summary>Fails the complete read with a bounded error instead of silently reducing its graph.</summary>
    private static void Require([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw new InvalidDataException("Required Trip content could not be projected.");
    }

    /// <summary>Maps public Trip fields, using prevalidated Segment projections when supplied.</summary>
    public static ApiTripDto ToApiDto(this Trip trip, IReadOnlyList<ApiTripSegmentDto> resolvedSegments)
    {
        if (trip == null)
        {
            throw new ArgumentNullException(nameof(trip));
        }
        ArgumentNullException.ThrowIfNull(resolvedSegments);

        return new ApiTripDto
        {
            Id = trip.Id,
            Name = trip.Name,
            Notes = RichNotes.Normalize(trip.Notes),
            IsPublic = trip.IsPublic,
            CenterLat = trip.CenterLat,
            CenterLon = trip.CenterLon,
            Zoom = trip.Zoom,
            CoverImageUrl = trip.CoverImageUrl,
            UpdatedAt = trip.UpdatedAt,
            Regions = trip.Regions?
                .Where(r => r != null)
                .OrderBy(r => r.DisplayOrder)
                .Select(ToApiDto)
                .ToList(),
            Segments = resolvedSegments.OrderBy(segment => segment.DisplayOrder).ToList(),
            Tags = trip.Tags?
                .Select(t => new ApiTagDto
                {
                    Id = t.Id,
                    Slug = t.Slug,
                    Name = t.Name
                })
                .ToList()
        };
    }

    /// <summary>Maps Region presentation fields and ordered children for existing callers.</summary>
    public static ApiTripRegionDto ToApiDto(this Region region)
    {
        if (region == null)
        {
            throw new ArgumentNullException(nameof(region));
        }

        return new ApiTripRegionDto
        {
            Id = region.Id,
            Name = region.Name,
            Notes = RichNotes.Normalize(region.Notes),
            DisplayOrder = region.DisplayOrder,
            CoverImageUrl = region.CoverImageUrl,
            Center = region.Center is Point pt ? new[] { pt.X, pt.Y } : null,
            Places = region.Places?
                .Where(p => p != null)
                .OrderBy(p => p.DisplayOrder)
                .Select(ToApiDto)
                .ToList(),
            Areas = region.Areas?
                .Where(a => a != null)
                .OrderBy(a => a.DisplayOrder)
                .Select(ToApiDto)
                .ToList()
        };
    }

    /// <summary>Preserves the mutation response's nullable projection of historical malformed locations.</summary>
    public static ApiTripPlaceDto ToApiDto(this Place place)
    {
        if (place == null)
        {
            throw new ArgumentNullException(nameof(place));
        }

        return new ApiTripPlaceDto
        {
            Id = place.Id,
            Name = place.Name,
            Notes = RichNotes.Normalize(place.Notes),
            Address = place.Address,
            ResolvedFeatureName = place.ResolvedFeatureName,
            ResolvedFeatureType = place.ResolvedFeatureType,
            IconName = place.IconName,
            MarkerColor = place.MarkerColor,
            DisplayOrder = place.DisplayOrder,
            Location = place.Location is Point pt && double.IsFinite(pt.X) && double.IsFinite(pt.Y)
                ? new[] { pt.X, pt.Y }
                : null
        };
    }

    /// <summary>Copies Area geometry for existing permissive callers without changing its stored SRID.</summary>
    public static ApiTripAreaDto ToApiDto(this Area area)
    {
        if (area == null)
        {
            return new ApiTripAreaDto();
        }

        string? geoJson = null;

        try
        {
            if (area.Geometry != null)
            {
                var publicGeometry = area.Geometry.Copy();
                publicGeometry.SRID = 4326;
                geoJson = _geoJsonWriter.Write(publicGeometry);
            }
        }
        catch
        {
            geoJson = null; // prevent any serialization exception from breaking API
        }

        return new ApiTripAreaDto
        {
            Id = area.Id,
            Name = area.Name,
            Notes = RichNotes.Normalize(area.Notes),
            DisplayOrder = area.DisplayOrder,
            FillHex = area.FillHex,
            GeometryGeoJson = geoJson
        };
    }

}
