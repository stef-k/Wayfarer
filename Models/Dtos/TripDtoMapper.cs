using System.Text.Json;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Models.Dtos;

public static class TripDtoMapper
{
    private static readonly GeoJsonWriter _geoJsonWriter = new();

    public static ApiTripDto ToApiDto(this Trip trip)
    {
        if (trip == null)
        {
            throw new ArgumentNullException(nameof(trip));
        }

        return new ApiTripDto
        {
            Id = trip.Id,
            Name = trip.Name,
            Notes = trip.Notes,
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
            Segments = trip.Segments?
                .Where(s => s != null)
                .OrderBy(s => s.DisplayOrder)
                .Select(ToApiDto)
                .ToList(),
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
            Notes = region.Notes,
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
            Notes = place.Notes,
            Address = place.Address,
            IconName = place.IconName,
            MarkerColor = place.MarkerColor,
            DisplayOrder = place.DisplayOrder,
            Location = place.Location is Point pt ? new[] { pt.X, pt.Y } : null
        };
    }

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
                if (area.Geometry.SRID != 4326)
                {
                    area.Geometry.SRID = 4326;
                }

                geoJson = _geoJsonWriter.Write(area.Geometry);
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
            Notes = area.Notes,
            DisplayOrder = area.DisplayOrder,
            FillHex = area.FillHex,
            GeometryGeoJson = geoJson
        };
    }

    public static ApiTripSegmentDto ToApiDto(this Segment segment)
    {
        if (segment == null)
        {
            return new ApiTripSegmentDto();
        }

        string? routeJson = null;

        try
        {
            if (segment.RouteGeometry != null)
            {
                if (segment.RouteGeometry.SRID != 4326)
                {
                    segment.RouteGeometry.SRID = 4326;
                }

                routeJson = _geoJsonWriter.Write(segment.RouteGeometry);
            }
        }
        catch
        {
            routeJson = null; // avoid crash from malformed route
        }

        return new ApiTripSegmentDto
        {
            Id = segment.Id,
            Mode = segment.Mode ?? "",
            Notes = segment.Notes,
            DisplayOrder = segment.DisplayOrder,
            EstimatedDistanceKm = segment.EstimatedDistanceKm,
            EstimatedDurationMinutes = segment.EstimatedDuration?.TotalMinutes,
            FromPlaceId = segment.FromPlaceId,
            ToPlaceId = segment.ToPlaceId,
            RouteJson = routeJson
        };
    }
}
