using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Writer-to-persistence contracts for typed native identities and independent destination order.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class NativeKmlGraphPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Equal UUIDs across all five types retain planning and routes, with independent IDs only on CreateNew.</summary>
    [PostgresFact]
    public async Task TypedIdentitiesSurviveWriterCreateNewAndReviewedReplacement()
    {
        var owner = await fixture.CreateUserAsync();
        var source = TypedTrip(owner.Id);
        fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext();
        db.Trips.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == source.Id);
        var xml = TripWayfarerKmlExporter.BuildKml(before, true);
        Assert.Equal(WayfarerKmlKind.NativeV5, WayfarerKmlParser.ClassifyAndParse(File(xml)).Kind);
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var created = await service.ImportWayfarerKmlAsync(File(xml), owner.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(created.TripId);
        await using var verify = fixture.CreateContext();
        var copy = await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == created.TripId);
        Assert.False(copy.IsPublic);
        Assert.False(copy.ShareProgressEnabled);
        AssertRemapped(before, copy);
        var originalXml = TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == source.Id), true);
        Assert.Equal(xml, originalXml);

        // Review and replacement attach planning by type, even when every type shares one UUID.
        before.Name = "Reviewed typed replacement";
        before.Regions.Single().Places.Single(p => p.Id == source.Id).Name = "Reviewed Place";
        xml = TripWayfarerKmlExporter.BuildKml(before, true);
        var review = await service.ReviewNativeAsync(File(xml), owner.Id);
        Assert.Equal(7, review.Planning.Count(r => r.Field == "planning" && r.State == "equal"));
        await service.ImportReviewedNativeAsync(File(xml), owner.Id, new(review.Token, []));
        var replaced = await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == source.Id);
        Assert.Equal(before.Name, replaced.Name);
        Assert.Equal("Reviewed Place", replaced.Regions.Single().Places.First(p => p.Id == source.Id).Name);
        var originals = NativeTripGraph.Targets(before).ToDictionary(t => (t.Kind, t.Id));
        foreach (var target in NativeTripGraph.Targets(replaced))
            Assert.True(PlanningCodec.Equal(originals[(target.Kind, target.Id)].Target.Planning, target.Target.Planning));
    }

    /// <summary>Another table's row neither authorizes a foreign same-type target nor blocks a valid new typed target.</summary>
    [PostgresFact]
    public async Task ReplacementChecksSameTypeOwnershipBeforePlanningRemap()
    {
        var owner = await fixture.CreateUserAsync();
        var foreign = await fixture.CreateUserAsync();
        var current = TypedTrip(owner.Id);
        var other = TypedTrip(foreign.Id);
        // This Region has no same-ID Place/Area, permitting a foreign Place collision in another table.
        var ownRegion = current.Regions.Single();
        ownRegion.Id = Guid.NewGuid();
        foreach (var place in ownRegion.Places) place.RegionId = ownRegion.Id;
        foreach (var area in ownRegion.Areas) area.RegionId = ownRegion.Id;
        var foreignPlaceId = ownRegion.Id;
        other.Regions.Single().Places.Add(new Place { Id = foreignPlaceId, RegionId = other.Id, UserId = foreign.Id, Name = "Foreign collision" });
        fixture.RegisterTrip(current.Id);
        fixture.RegisterTrip(other.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(current, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == current.Id);
        var initial = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var foreignInitial = TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(db).SingleAsync(t => t.Id == other.Id), true);
        // UUID belongs to a foreign Place and also to our retained Region: only the Place ownership matters.
        incoming.Regions.Single().Places.Add(new Place { Id = foreignPlaceId, RegionId = ownRegion.Id, UserId = owner.Id, Name = "Forbidden" });
        // Remove the retained same-ID Place in this candidate, avoiding a same-type duplicate.
        incoming.Regions.Single().Places.Remove(incoming.Regions.Single().Places.First(p => p.Name == "From"));
        incoming.Segments.Clear();
        var forbidden = TripWayfarerKmlExporter.BuildKml(incoming, true);
        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ReviewNativeAsync(File(forbidden), owner.Id));
        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportReviewedNativeAsync(File(forbidden), owner.Id, new("invalid", [])));
        await using var verify = fixture.CreateContext();
        Assert.Equal(initial, TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == current.Id), true));
        Assert.Equal(foreignInitial, TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == other.Id), true));

        // A new Area may share a UUID with a foreign Place when no Area owns that UUID.
        incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == current.Id);
        var permittedId = other.Regions.Single().Places.Single(p => p.Name == "Via").Id;
        incoming.Regions.Single().Areas.Add(new Area { Id = permittedId, RegionId = ownRegion.Id, Name = "Permitted typed collision", Geometry = Polygon() });
        var permitted = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var review = await service.ReviewNativeAsync(File(permitted), owner.Id);
        await service.ImportReviewedNativeAsync(File(permitted), owner.Id, new(review.Token, []));
        var saved = await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == current.Id);
        Assert.Contains(saved.Regions.Single().Areas, a => a.Id == permittedId);
        Assert.True(await verify.Places.AnyAsync(p => p.Id == permittedId && p.UserId == foreign.Id));
    }

    /// <summary>Same-type duplicates across parents and empty IDs fail before any create or replacement mutation.</summary>
    [PostgresTheory]
    [InlineData("place")]
    [InlineData("area")]
    [InlineData("empty")]
    public async Task InvalidSameTypeIdentitiesLeaveCurrentGraphUnchanged(string kind)
    {
        var owner = await fixture.CreateUserAsync();
        var trip = TypedTrip(owner.Id);
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var initial = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner.Id, Name = "Second parent" };
        incoming.Regions.Add(region);
        if (kind == "place") region.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = owner.Id, Name = "Duplicate" });
        else if (kind == "area") region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Duplicate", Geometry = Polygon() });
        // Tamper valid writer output so malformed uploads reach reader admission, rather than the writer's own guard.
        var malformed = XDocument.Parse(TripWayfarerKmlExporter.BuildKml(incoming, true));
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var field = kind == "place" ? "PlaceId" : kind == "area" ? "AreaId" : "RegionId";
        malformed.Descendants(k + "Folder").Single(f => f.Element(k + "name")?.Value == "Second parent")
            .Descendants(k + "Data").Single(d => (string?)d.Attribute("name") == field).Element(k + "value")!.Value =
            (kind == "empty" ? Guid.Empty : trip.Id).ToString("D");
        var xml = malformed.ToString();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        await Assert.ThrowsAsync<FormatException>(() => service.ImportWayfarerKmlAsync(File(xml), owner.Id, TripImportMode.CreateNew));
        await Assert.ThrowsAsync<FormatException>(() => service.ReviewNativeAsync(File(xml), owner.Id));
        await using var verify = fixture.CreateContext();
        Assert.Equal(new[] { trip.Id }, await verify.Trips.Where(t => t.UserId == owner.Id).Select(t => t.Id).ToArrayAsync());
        Assert.Equal(initial, TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == trip.Id), true));
    }

    /// <summary>Ties, null Place orders and differing Area names survive writer/CreateNew/reload without source changes.</summary>
    [PostgresFact]
    public async Task NativeCreateNewRetainsAllVisibleSiblingSequences()
    {
        var owner = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Tied native source" };
        for (var i = 1; i <= 8; i++)
        {
            trip.Regions.Add(new Region { Id = Key(1, i), TripId = trip.Id, UserId = owner.Id, Name = $"Region {i}", DisplayOrder = 0 });
            trip.Segments.Add(new Segment { Id = Key(4, i), TripId = trip.Id, UserId = owner.Id, Title = $"Segment {i}", Mode = "", DisplayOrder = 0 });
        }
        trip.Regions.Add(new Region { Id = Key(1, 9), TripId = trip.Id, UserId = owner.Id, Name = "Unassigned Places", DisplayOrder = -1 });
        var parent = trip.Regions.First();
        for (var i = 1; i <= 8; i++)
        {
            parent.Places.Add(new Place { Id = Key(2, i), RegionId = parent.Id, UserId = owner.Id,
                Name = $"Place {i}", DisplayOrder = i <= 6 ? 0 : null });
            parent.Areas.Add(new Area { Id = Key(3, i), RegionId = parent.Id,
                Name = i % 2 == 0 ? "A area" : "Z area", Notes = $"Area {i}", DisplayOrder = 0, Geometry = Polygon() });
        }
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var original = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var before = Sequences(original);
        var xml = TripWayfarerKmlExporter.BuildKml(original);
        var read = WayfarerKmlParser.ClassifyAndParse(File(xml)).Document!;
        Assert.Equal(before, Sequences(WayfarerKmlParser.Parse(File(xml))));
        Assert.Equal(new[] { "Place 1", "Place 2", "Place 3", "Place 4", "Place 5", "Place 6", "Place 7", "Place 8" }, read.Regions.Single(r => r.Name == "Region 1").Places.Select(p => p.Name));
        Assert.Equal(new[] { "Area 2", "Area 4", "Area 6", "Area 8", "Area 1", "Area 3", "Area 5", "Area 7" },
            read.Regions.Single(r => r.Name == "Region 1").Areas.Select(a => a.Notes));
        var result = await new TripImportService(db, NullLogger<TripImportService>.Instance)
            .ImportWayfarerKmlAsync(File(xml), owner.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(result.TripId);
        await using var verify = fixture.CreateContext();
        var after = await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == result.TripId);
        Assert.Equal(before, Sequences(after));
        Assert.Equal(xml, TripWayfarerKmlExporter.BuildKml(await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == trip.Id)));
        Assert.Equal(xml, TripWayfarerKmlExporter.BuildKml(original));
        AssertRemapped(original, after);
        AssertNormalized(after.Regions.Select(r => (int?)r.DisplayOrder));
        AssertNormalized(after.Segments.Select(s => (int?)s.DisplayOrder));
        foreach (var region in after.Regions)
        {
            AssertNormalized(region.Places.Select(p => p.DisplayOrder));
            AssertNormalized(region.Areas.Select(a => (int?)a.DisplayOrder));
        }
        // Ordinary reviewed replacement preserves raw source ties/nulls and retained IDs.
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var review = await service.ReviewNativeAsync(File(xml), owner.Id);
        await service.ImportReviewedNativeAsync(File(xml), owner.Id, new(review.Token, []));
        var retained = await NativeTripGraph.Query(verify).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(xml.Replace(original.UpdatedAt.ToString("O"), "TIME"),
            TripWayfarerKmlExporter.BuildKml(retained).Replace(retained.UpdatedAt.ToString("O"), "TIME"));
    }

    /// <summary>Creates a valid typed collision graph with distinguishable planning on every target and ordered Via.</summary>
    private static Trip TypedTrip(string owner)
    {
        var id = Guid.NewGuid();
        var trip = new Trip { Id = id, UserId = owner, Name = "Typed source", Planning = Plan("trip") };
        var region = new Region { Id = id, TripId = id, UserId = owner, Name = "Typed Region", Planning = Plan("region") };
        trip.Regions.Add(region);
        foreach (var (name, index) in new[] { ("From", 0), ("Via", 1), ("To", 2) })
            region.Places.Add(new Place { Id = index == 0 ? id : Guid.NewGuid(), RegionId = id, UserId = owner,
                Name = name, DisplayOrder = index, Location = new Point(index, 0) { SRID = 4326 }, Planning = Plan(name) });
        region.Areas.Add(new Area { Id = id, RegionId = id, Name = "Typed Area", Geometry = Polygon(), Planning = Plan("area") });
        var places = region.Places.ToArray();
        var segment = new Segment { Id = id, TripId = id, UserId = owner, Title = "Typed Segment", Mode = "",
            FromPlaceId = places[0].Id, ToPlaceId = places[2].Id, EstimatedDistanceKm = SegmentMeasurementCalculator.CalculateDistance([new(0, 0), new(1, 0), new(2, 0)]).RoundedKilometres,
            EstimatedDuration = TimeSpan.FromTicks(123456789), EstimatedDurationSource = EstimatedDurationSource.Manual, Planning = Plan("segment") };
        segment.Waypoints.Add(new SegmentWaypoint { SegmentId = id, PlaceId = places[1].Id, Position = 0 });
        trip.Segments.Add(segment);
        return trip;
    }

    /// <summary>Proves every typed identity, parent and route reference is independent after CreateNew.</summary>
    private static void AssertRemapped(Trip source, Trip copy)
    {
        var old = NativeTripGraph.Targets(source).ToDictionary(t => (t.Kind, Name: t.Target is Area a ? a.Notes ?? a.Name : t.Name));
        var targets = NativeTripGraph.Targets(copy).ToArray();
        Assert.Equal(targets.Length, targets.Select(t => t.Id).Distinct().Count());
        foreach (var target in targets)
        {
            var before = old[(target.Kind, target.Target is Area a ? a.Notes ?? a.Name : target.Name)];
            Assert.NotEqual(before.Id, target.Id);
            if (before.Target.Planning is { } planning)
            {
                Assert.NotNull(target.Target.Planning);
                Assert.NotEqual(planning.Entries[0].Id, target.Target.Planning.Entries[0].Id);
                Assert.Equal(planning.Entries[0], target.Target.Planning.Entries[0] with { Id = planning.Entries[0].Id });
            }
        }
        Assert.All(copy.Regions, r => Assert.Equal(copy.Id, r.TripId));
        Assert.All(copy.Regions, r => Assert.All(r.Places, p => Assert.Equal(r.Id, p.RegionId)));
        Assert.All(copy.Regions, r => Assert.All(r.Areas, a => Assert.Equal(r.Id, a.RegionId)));
        var places = copy.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id);
        foreach (var segment in copy.Segments)
        {
            Assert.Equal(copy.Id, segment.TripId);
            var before = source.Segments.Single(s => s.Title == segment.Title);
            Assert.Equal(before.FromPlace?.Name, segment.FromPlaceId is { } from ? places[from].Name : null);
            Assert.Equal(before.ToPlace?.Name, segment.ToPlaceId is { } to ? places[to].Name : null);
            Assert.Equal(before.Waypoints.OrderBy(w => w.Position).Select(w => w.Place.Name), segment.Waypoints.OrderBy(w => w.Position).Select(w => places[w.PlaceId].Name));
            Assert.Equal(before.EstimatedDuration, segment.EstimatedDuration);
            Assert.Equal(before.EstimatedDistanceKm, segment.EstimatedDistanceKm);
        }
    }

    /// <summary>Checks the newly created list has distinct contiguous nonnegative values, including formerly null Places.</summary>
    private static void AssertNormalized(IEnumerable<int?> values)
    {
        var orders = values.ToArray();
        Assert.Equal(Enumerable.Range(0, orders.Length).Select(i => (int?)i), orders.OrderBy(i => i));
    }

    private static PlanningAggregate Plan(string label) => new(1, [new(Guid.NewGuid(), new("at", new("date", "2028-01-01")), label)]);
    private static Polygon Polygon() => new(new LinearRing([new(1, 1), new(2, 1), new(2, 2), new(1, 1)])) { SRID = 4326 };
    private static MemoryStream File(string xml) => new(Encoding.UTF8.GetBytes(xml));
    private static Guid Key(int kind, int index) => Guid.Parse($"9110000{kind}-0000-0000-0000-{index:000000000000}");

    /// <summary>Uses the established product comparers, retaining unique labels when Area names tie.</summary>
    private static string Sequences(Trip trip)
    {
        var parent = trip.Regions.Single(r => r.Name == "Region 1");
        return JsonSerializer.Serialize(new
        {
            regions = trip.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => r.Name),
            places = parent.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Name),
            areas = parent.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(a => a.Notes),
            segments = trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => s.Title)
        });
    }
}
