using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises both durable Visit snapshot writers after their existing truncation step.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RichNotesSnapshotPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_CanonicalizesFinalTruncatedValueWithoutRewritingPlace(bool backfill)
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Snapshot" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Region" };
        const string notes = "<p onclick='bad()'><s>safe</s></p><script>bad()</script><img src='https://example.test/unfinished";
        var place = new Place { Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Place",
            Location = new Point(1, 1) { SRID = 4326 }, Notes = notes };
        db.AddRange(trip, region, place);
        await db.SaveChangesAsync();
        var settings = new ApplicationSettings { VisitedRequiredHits = 2,
            VisitedMinRadiusMeters = 35, VisitedMaxRadiusMeters = 100, VisitedMaxSearchRadiusMeters = 150,
            VisitedPlaceNotesSnapshotMaxHtmlChars = notes.Length - 2 };
        var settingsService = Mock.Of<IApplicationSettingsService>(service => service.GetSettings() == settings);
        if (backfill)
        {
            var result = await new VisitBackfillService(db, settingsService, NullLogger<VisitBackfillService>.Instance)
                .ApplyAsync(user.Id, trip.Id, new BackfillApplyRequestDto { CreateVisits = [new()
                { PlaceId = place.Id, FirstSeenUtc = DateTime.UtcNow.AddMinutes(-2), LastSeenUtc = DateTime.UtcNow }] });
            Assert.True(result.Success);
        }
        else
        {
            var service = new PlaceVisitDetectionService(db, settingsService, new SseService(), NullLogger<PlaceVisitDetectionService>.Instance);
            await service.ProcessPingAsync(user.Id, place.Location, 1);
            await service.ProcessPingAsync(user.Id, place.Location, 1);
        }
        var visit = await db.PlaceVisitEvents.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal("<p><s>safe</s></p>", visit.NotesHtml);
        Assert.Equal(notes, place.Notes);
    }
}
