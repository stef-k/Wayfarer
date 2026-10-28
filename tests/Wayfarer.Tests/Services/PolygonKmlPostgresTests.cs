using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real Trip import transaction boundaries retain complete rings or leave the stored graph untouched.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PolygonKmlPostgresTests(PostgresImportTestFixture fixture)
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    /// <summary>Fresh database reads prove both exporters/importers retain one and multiple holes without modifying the source.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTrip_PreservesPersistedRings(bool google)
    {
        var original = await SeedTripAsync();
        string xml;
        await using (var export = fixture.CreateContext())
            xml = Export(export, original.Id, google);
        Guid importedId;
        await using (var import = fixture.CreateContext())
        {
            using var stream = Stream(xml);
            importedId = (await Importer(import).ImportWayfarerKmlAsync(stream, original.UserId, TripImportMode.CreateNew)).TripId;
            fixture.RegisterTrip(importedId);
        }
        await using (var verification = fixture.CreateContext())
        {
            AssertTrip(original, await ReadTripAsync(verification, original.Id));
            AssertAreas(original, await ReadTripAsync(verification, importedId));
            if (!google) xml = Export(verification, importedId, false);
        }
        if (!google)
        {
            // Native authoritative replacement is a second supported persistence path.
            await using var upsert = fixture.CreateContext();
            using var stream = Stream(xml);
            var result = await Importer(upsert).ImportWayfarerKmlAsync(stream, original.UserId, TripImportMode.Upsert);
            Assert.Equal(importedId, result.TripId);
            await using var verification = fixture.CreateContext();
            AssertAreas(original, await ReadTripAsync(verification, importedId));
        }
    }

    /// <summary>A valid earlier Area followed by an invalid later hole cannot create or partially replace a Trip.</summary>
    [PostgresTheory]
    [InlineData(false, TripImportMode.CreateNew)]
    [InlineData(false, TripImportMode.Upsert)]
    [InlineData(true, TripImportMode.CreateNew)]
    public async Task InvalidLaterHole_LeavesStoredGraphUnchanged(bool google, TripImportMode mode)
    {
        var original = await SeedTripAsync();
        XDocument xml;
        await using (var export = fixture.CreateContext())
            xml = XDocument.Parse(Export(export, original.Id, google));
        xml.Descendants(Kml + "Document").Single().Element(Kml + "name")!.Value = "Rejected replacement";
        xml.Descendants(Kml + "Polygon").Last().Elements(Kml + "innerBoundaryIs").Last()
            .Descendants(Kml + "coordinates").Single().Value = "20,20 21,20 21,21 20,20";
        await using (var import = fixture.CreateContext())
        {
            using var stream = Stream(xml.ToString());
            await Assert.ThrowsAsync<FormatException>(() =>
                Importer(import).ImportWayfarerKmlAsync(stream, original.UserId, mode));
            Assert.Empty(import.ChangeTracker.Entries());
        }
        await using var verification = fixture.CreateContext();
        Assert.Equal(new[] { original.Id }, await verification.Trips.Where(trip => trip.UserId == original.UserId)
            .Select(trip => trip.Id).ToArrayAsync());
        var persisted = await ReadTripAsync(verification, original.Id);
        AssertTrip(original, persisted);
        Assert.Empty(await verification.Places.Where(place => place.UserId == original.UserId).ToListAsync());
        Assert.Empty(await verification.Segments.Where(segment => segment.UserId == original.UserId).ToListAsync());
    }

    /// <summary>Seeds only registered fixture-owned data, with differently oriented holes in the second Area.</summary>
    private async Task<Trip> SeedTripAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Stored rings", Notes = "<p>Keep me</p>", UpdatedAt = DateTime.UtcNow };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "Region" };
        foreach (var holes in new[] { 1, 2 })
        {
            var polygon = (Polygon)new WKTReader().Read("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1), (6 6, 8 6, 8 8, 6 8, 6 6))");
            polygon = new Polygon((LinearRing)polygon.ExteriorRing.Copy(), Enumerable.Range(0, holes)
                .Select(index => (LinearRing)polygon.GetInteriorRingN(index).Copy()).ToArray()) { SRID = 4326 };
            region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = $"Area {holes}", DisplayOrder = holes, Geometry = polygon });
        }
        trip.Regions.Add(region);
        fixture.RegisterTrip(trip.Id);
        await using var seed = fixture.CreateContext();
        seed.Trips.Add(trip);
        await seed.SaveChangesAsync();
        // Compare against the stored timestamp at PostgreSQL precision, after the normal save-time stamp.
        return await ReadTripAsync(seed, trip.Id);
    }

    /// <summary>Compares original identities and metadata as well as exact rings after an export or rejected import.</summary>
    private static void AssertTrip(Trip expected, Trip actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.Regions.Select(region => region.Id), actual.Regions.Select(region => region.Id));
        Assert.Equal(expected.Regions.Single().Areas.OrderBy(area => area.Name).Select(area => area.Id),
            actual.Regions.Single().Areas.OrderBy(area => area.Name).Select(area => area.Id));
        AssertAreas(expected, actual);
    }

    /// <summary>Checks persisted coordinates in order and topology, independently of import identity remapping.</summary>
    private static void AssertAreas(Trip expected, Trip actual)
    {
        var areas = actual.Regions.SelectMany(region => region.Areas).OrderBy(area => area.Name).ToArray();
        var originals = expected.Regions.SelectMany(region => region.Areas).OrderBy(area => area.Name).ToArray();
        Assert.Equal(originals.Length, areas.Length);
        for (var index = 0; index < originals.Length; index++)
        {
            var polygon = areas[index].Geometry;
            Assert.Equal(4326, polygon.SRID);
            Assert.True(polygon.IsValid);
            Assert.Equal(index + 1, polygon.NumInteriorRings);
            Assert.True(originals[index].Geometry.EqualsExact(polygon));
            Assert.True(originals[index].Geometry.EqualsTopologically(polygon));
        }
    }

    /// <summary>Uses the actual KML export service with no browser/PDF dependencies.</summary>
    private static string Export(ApplicationDbContext db, Guid tripId, bool google)
    {
        var service = new TripExportService(db, null!, null!, null!, null!, NullLogger<TripExportService>.Instance,
            Mock.Of<IConfiguration>(), null!, Mock.Of<IImageProxyService>());
        return google ? service.GenerateGoogleMyMapsKml(tripId) : service.GenerateWayfarerKml(tripId);
    }

    private static TripImportService Importer(ApplicationDbContext db) => new(db, NullLogger<TripImportService>.Instance);
    private static MemoryStream Stream(string xml) => new(Encoding.UTF8.GetBytes(xml));
    private static Task<Trip> ReadTripAsync(ApplicationDbContext db, Guid id) => db.Trips.AsNoTracking()
        .Include(trip => trip.Regions).ThenInclude(region => region.Areas).SingleAsync(trip => trip.Id == id);
}
