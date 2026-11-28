using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Publishes exact owner comparisons from a single loaded Region, Place and Area graph.</summary>
internal static class TripStructureOwnerSnapshot
{
    internal const string Contract = "trip-region-place-area-receipt-1";

    /// <summary>Projects all comparison rows before presentation validation or GeoJSON serialization.</summary>
    internal static object Project(Trip trip, ApplicationDbContext db, CancellationToken token)
    {
        if (!db.Entry(trip).Collection(item => item.Regions).IsLoaded || trip.Id == Guid.Empty
            || trip.Regions.Count(region => region.Name == "Unassigned Places") > 1)
            throw InvalidSnapshot();
        return new
        {
            contract = Contract, subjectId = trip.UserId, tripId = trip.Id,
            regions = trip.Regions.OrderBy(region => region.Id).Select(region => ProjectRegion(trip, region, db, token)).ToArray()
        };
    }

    /// <summary>Validates the loaded owned chain before projecting any of the child comparisons.</summary>
    private static object ProjectRegion(Trip trip, Region region, ApplicationDbContext db, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (region.Id == Guid.Empty || region.TripId != trip.Id || region.UserId != trip.UserId
            || !db.Entry(region).Collection(item => item.Places).IsLoaded
            || !db.Entry(region).Collection(item => item.Areas).IsLoaded
            || region.Places.Any(place => place.Id == Guid.Empty || place.RegionId != region.Id || place.UserId != trip.UserId)
            || region.Areas.Any(area => area.Id == Guid.Empty || area.RegionId != region.Id))
            throw InvalidSnapshot();
        return new
        {
            region.Id, region.DisplayOrder, isCanonical = region.Name == "Unassigned Places",
            region.Name, notes = RichNotes.Normalize(region.Notes), region.CoverImageUrl,
            center = RegionCenter(region.Center), region.Planning,
            places = region.Places.OrderBy(place => place.Id).Select(ProjectPlace).ToArray(),
            areas = region.Areas.OrderBy(area => area.Id).Select(area => ProjectArea(area, token)).ToArray()
        };
    }

    /// <summary>Publishes finite historical centers without imposing the desired-value bounds.</summary>
    private static double[]? RegionCenter(Point? center) => center switch
    {
        null => null,
        { IsEmpty: false } value when double.IsFinite(value.X) && double.IsFinite(value.Y) => [value.X, value.Y],
        _ => throw InvalidSnapshot()
    };

    /// <summary>Reuses the Place comparison projection, including its nullable historical location.</summary>
    private static object ProjectPlace(Place place)
    {
        var value = place.ToApiDto();
        return new
        {
            value.Id, value.Name, value.Notes, value.Address, value.Location, value.IconName, value.MarkerColor,
            value.DisplayOrder, value.ResolvedFeatureName, value.ResolvedFeatureType, value.AddressEnrichmentProvider,
            value.AddressEnrichmentStorageMode, value.AddressEnrichedAt, place.Planning
        };
    }

    /// <summary>Reads the stored fill and fingerprints the exact saved Polygon independently of display geometry.</summary>
    private static object ProjectArea(Area area, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new
        {
            area.Id, area.DisplayOrder, area.Name, notes = RichNotes.Normalize(area.Notes), area.FillHex,
            geometryIdentity = AreaGeometryIdentity.For(area.Geometry ?? throw InvalidSnapshot()), area.Planning
        };
    }

    /// <summary>Fails the whole comparison instead of presenting a partial base for an edit.</summary>
    private static InvalidDataException InvalidSnapshot() => new("Required Trip comparisons could not be projected.");
}
