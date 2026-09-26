using Location = Wayfarer.Models.Location;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves rich transport encoding, durable normalization and safe historical exports.</summary>
public sealed class RichNotesImportExportTests : TestBase
{
    private const string Safe = "<p class=\"ql-align-justify\"><s>rich</s> &amp; text</p>";
    private const string Unsafe = Safe + "<script>bad()</script><img src='data:image/png,x'>";

    [Theory]
    [InlineData(TripImportMode.CreateNew)]
    [InlineData(TripImportMode.Upsert)]
    public async Task Native_AllFiveDomains_NormalizeOnImportAndHistoricalExport(TripImportMode mode)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user.Id, "Rich notes");
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Region", Notes = Unsafe };
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "Place", Notes = Unsafe, Location = new Point(1, 1) { SRID = 4326 } };
        var area = new Area { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, Name = "Area", Notes = Unsafe,
            Geometry = new Polygon(new LinearRing([new(0, 0), new(1, 0), new(1, 1), new(0, 0)])) { SRID = 4326 } };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = "walk", Notes = Unsafe };
        trip.Notes = Unsafe;
        trip.Regions = [region]; region.Places = [place]; region.Areas = [area]; trip.Segments = [segment];
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var exported = TripWayfarerKmlExporter.BuildKml(trip);
        Assert.All(NoteValues(exported), note => Assert.Equal(Safe, note));
        Assert.Equal(Unsafe, trip.Notes);
        Assert.Equal(Unsafe, place.Notes);
        // Reintroduce hostile HTML inside XML text to exercise ingress independently of export safety.
        var document = XDocument.Parse(exported);
        foreach (var data in document.Descendants().Where(node => (string?)node.Attribute("name") == "NotesHtml"))
            data.Elements().Single().Value = Unsafe;
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        var result = await service.ImportWayfarerKmlAsync(input, user.Id, mode);
        var stored = await db.Trips.Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas).Include(t => t.Segments).Include(t => t.Tags)
            .SingleAsync(t => t.Id == result.TripId);
        var again = TripWayfarerKmlExporter.BuildKml(stored);
        Assert.Equal(5, NoteValues(again).Count);
        Assert.All(NoteValues(again), note => Assert.Equal(Safe, note));
        Assert.Equal(Safe, stored.Notes);
        Assert.Equal(Safe, stored.Regions.Single().Notes);
        Assert.Equal(Safe, stored.Regions.Single().Places.Single().Notes);
        Assert.Equal(Safe, stored.Regions.Single().Areas.Single().Notes);
        Assert.Equal(Safe, stored.Segments.Single().Notes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Generic_PlaceAndAreaDescriptionsRemainRichAfterXmlDecode(bool cdata)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(); db.Users.Add(user); await db.SaveChangesAsync();
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var document = new XDocument(new XElement(k + "kml", new XElement(k + "Document",
            new XElement(k + "Folder", new XElement(k + "name", "Region"),
                new XElement(k + "Placemark", new XElement(k + "name", "Place"),
                    new XElement(k + "description", cdata ? new XCData(Unsafe) : new XText(Unsafe)),
                    new XElement(k + "Point", new XElement(k + "coordinates", "1,1"))),
                new XElement(k + "Placemark", new XElement(k + "name", "Area"),
                    new XElement(k + "description", cdata ? new XCData(Unsafe) : new XText(Unsafe)),
                    new XElement(k + "Polygon", new XElement(k + "outerBoundaryIs", new XElement(k + "LinearRing",
                        new XElement(k + "coordinates", "0,0 1,0 1,1 0,0")))))))));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        await new TripImportService(db, NullLogger<TripImportService>.Instance)
            .ImportWayfarerKmlAsync(input, user.Id, TripImportMode.CreateNew);
        Assert.Equal(Safe, db.Places.Single().Notes);
        Assert.Equal(Safe, db.Areas.Single().Notes);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("geojson")]
    [InlineData("gpx")]
    [InlineData("kml")]
    public async Task Location_ExportsAndCommonBatchPreserveSafeRichPayload(string format)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(); db.Users.Add(user);
        var location = new Location { UserId = user.Id, Coordinates = new Point(1, 1) { SRID = 4326 },
            TimeZoneId = "UTC", Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, Notes = Unsafe };
        db.Locations.Add(location); await db.SaveChangesAsync();
        var controller = new LocationExportController(db)
        { ControllerContext = new() { HttpContext = BuildHttpContextWithUser(user.Id) } };
        var response = format switch
        {
            "csv" => await controller.Csv(), "geojson" => await controller.GeoJson(),
            "gpx" => await controller.Gpx(), _ => await controller.Kml()
        };
        var file = Assert.IsType<FileStreamResult>(response);
        using var reader = new StreamReader(file.FileStream);
        var payload = await reader.ReadToEndAsync();
        Assert.DoesNotContain("bad()", payload);
        Assert.DoesNotContain("data:image", payload);
        Assert.Contains("ql-align-justify", payload);
        ILocationDataParser parser = format switch
        {
            "csv" => new CsvLocationParser(NullLogger<CsvLocationParser>.Instance),
            "geojson" => new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance),
            "gpx" => new GpxLocationParser(NullLogger<GpxLocationParser>.Instance),
            _ => new KmlLocationParser(NullLogger<KmlLocationParser>.Instance)
        };
        using var decodedStream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var decoded = new List<Location>();
        await foreach (var row in parser.ParseAsync(decodedStream, user.Id)) decoded.Add(row);
        Assert.Equal(Safe, Assert.Single(decoded).Notes);
        Assert.Equal(Unsafe, location.Notes);
        Assert.Equal(EntityState.Unchanged, db.Entry(location).State);
        var imported = new Location { UserId = user.Id, Coordinates = new Point(2, 2) { SRID = 4326 },
            TimeZoneId = "UTC", Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, Notes = Unsafe };
        await LocationImportDeduplicator.InsertAsync(db, [imported], user.Id, CancellationToken.None);
        Assert.Equal(Safe, imported.Notes);
    }

    private static List<string> NoteValues(string kml) => XDocument.Parse(kml).Descendants()
        .Where(node => (string?)node.Attribute("name") == "NotesHtml").Select(node => node.Elements().Single().Value).ToList();
}
