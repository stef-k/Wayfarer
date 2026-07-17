using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Focused in-memory coverage for deterministic KML import tag reconciliation.</summary>
public class TripImportTagReconcilerTests : TestBase
{
    [Fact]
    public async Task ReconcileAsync_ReusesStoredCasing_AndCreatesOneMissingTag()
    {
        var db = CreateDbContext();
        db.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = "Hike", Slug = "hike" });
        await db.SaveChangesAsync();
        var reconciler = CreateReconciler(db);

        var tags = await reconciler.ReconcileAsync(new[] { " hike ", "HIKE", "trail", " TRAIL ", "", " " });

        Assert.Equal(new[] { "Hike", "trail" }, tags.Select(tag => tag.Name));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ReconcileAsync_RejectsNonemptyUnrepresentableToken()
    {
        var db = CreateDbContext();

        await Assert.ThrowsAsync<TripImportValidationException>(() => CreateReconciler(db).ReconcileAsync(new[] { "---" }));

        Assert.Empty(db.Tags);
    }

    [Theory]
    [InlineData(TripImportMode.Auto)]
    [InlineData(TripImportMode.CreateNew)]
    public async Task ImportWayfarerKmlAsync_CloneModesAttachReconciledTags(TripImportMode mode)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance, CreateReconciler(db));

        var id = await service.ImportWayfarerKmlAsync(ToStream(CreateKml(Guid.NewGuid(), "coast")), user.Id, mode);

        Assert.Equal("coast", (await db.Trips.Include(trip => trip.Tags).SingleAsync(trip => trip.Id == id)).Tags.Single().Slug);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_UpsertReplacesStaleTags()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var stale = new Tag { Id = Guid.NewGuid(), Name = "stale", Slug = "stale" };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Existing", Tags = new List<Tag> { stale } };
        trip.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "Unassigned Places", Places = new List<Place>() });
        db.AddRange(user, stale, trip);
        await db.SaveChangesAsync();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance, CreateReconciler(db));

        await service.ImportWayfarerKmlAsync(ToStream(CreateKml(trip.Id, "fresh")), user.Id, TripImportMode.Upsert);

        Assert.Equal(new[] { "fresh" }, (await db.Trips.Include(value => value.Tags).SingleAsync(value => value.Id == trip.Id)).Tags.Select(tag => tag.Slug));
    }

    private static TripImportTagReconciler CreateReconciler(ApplicationDbContext db) => new(db, NullLogger<TripImportTagReconciler>.Instance);

    private static MemoryStream ToStream(string kml) => new(Encoding.UTF8.GetBytes(kml));

    private static string CreateKml(Guid id, string tags) => $@"<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Trip</name><ExtendedData>
<Data name=""TripId""><value>{id}</value></Data><Data name=""Tags""><value>{tags}</value></Data></ExtendedData></Document></kml>";
}
