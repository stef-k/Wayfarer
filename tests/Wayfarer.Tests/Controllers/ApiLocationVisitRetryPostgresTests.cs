using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises keyed capture retry ordering through the real controller and PostGIS visit detector.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiLocationVisitRetryPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>A failed Location insert and its retry supply one hit; only a different capture confirms a visit.</summary>
    [PostgresFact]
    public async Task LogLocation_FailedKeyedWriteThenRetry_RequiresDistinctObservationToConfirmVisit()
    {
        var user = await SeedPlaceAsync();
        var key = Guid.NewGuid();
        var dto = ApiLocationAdmissionTests.Dto();
        await using (var failedDb = fixture.CreateContext(new FailLocationWrite()))
        {
            var result = await Controller(failedDb, user.Id, key).LogLocation(dto);
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        await using (var afterFailure = fixture.CreateContext())
        {
            Assert.False(await afterFailure.Locations.AnyAsync(row => row.UserId == user.Id));
            output.WriteLine("After 500: locations=0, candidate hits={0}",
                await afterFailure.PlaceVisitCandidates.Where(row => row.UserId == user.Id)
                    .Select(row => (int?)row.ConsecutiveHits).SingleOrDefaultAsync());
        }

        await using (var retryDb = fixture.CreateContext())
            AssertSaved(await Controller(retryDb, user.Id, key).LogLocation(dto));

        await using (var afterRetry = fixture.CreateContext())
        {
            var location = Assert.Single(await afterRetry.Locations.Where(row => row.UserId == user.Id).ToListAsync());
            Assert.Equal(key, location.IdempotencyKey);
            var visits = await afterRetry.PlaceVisitEvents.Where(row => row.UserId == user.Id).ToListAsync();
            output.WriteLine("After same-key 200 retry: locations=1, confirmed visits={0}", visits.Count);
            Assert.Empty(visits);
            Assert.Equal(1, (await afterRetry.PlaceVisitCandidates.SingleAsync(row => row.UserId == user.Id)).ConsecutiveHits);
        }

        // A persisted replay must leave the one-hit candidate unchanged.
        await using (var replayDb = fixture.CreateContext())
            AssertSaved(await Controller(replayDb, user.Id, key).LogLocation(dto));
        await using (var afterReplay = fixture.CreateContext())
        {
            Assert.False(await afterReplay.PlaceVisitEvents.AnyAsync(row => row.UserId == user.Id));
            Assert.Equal(1, (await afterReplay.PlaceVisitCandidates.SingleAsync(row => row.UserId == user.Id)).ConsecutiveHits);
        }

        // About 22 metres apart: outside the 15m storage threshold and inside the 35m visit radius.
        dto.Longitude += 0.0002;
        dto.Timestamp = dto.Timestamp.AddMinutes(6);
        await using (var distinctDb = fixture.CreateContext())
            AssertSaved(await Controller(distinctDb, user.Id, Guid.NewGuid()).LogLocation(dto));
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.Locations.CountAsync(row => row.UserId == user.Id));
        Assert.Single(await verify.PlaceVisitEvents.Where(row => row.UserId == user.Id).ToListAsync());
        Assert.False(await verify.PlaceVisitCandidates.AnyAsync(row => row.UserId == user.Id));
    }

    /// <summary>Seeds only fixture-owned identity, token and spatial trip data for the default two-hit policy.</summary>
    private async Task<ApplicationUser> SeedPlaceAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Keyed visit retry");
        fixture.RegisterTrip(trip.Id);
        var region = TestDataFixtures.CreateRegion(trip);
        var place = TestDataFixtures.CreatePlace(region, latitude: 10, longitude: 20);
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "phone", Token = user.Id });
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Uses real spatial visit persistence with default settings and the existing provider-free controller fixture.</summary>
    private static LocationController Controller(ApplicationDbContext db, string userId, Guid? key)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        var visits = new PlaceVisitDetectionService(db, settings.Object, new SseService(),
            NullLogger<PlaceVisitDetectionService>.Instance);
        var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8), visits,
            settingsService: settings.Object);
        controller.Request.Headers.Authorization = $"Bearer {userId}";
        if (key.HasValue) controller.Request.Headers["Idempotency-Key"] = key.Value.ToString("D");
        return controller;
    }

    /// <summary>Checks the unchanged automatic-ingestion success response.</summary>
    private static void AssertSaved(IActionResult result)
    {
        var response = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.False(response.GetProperty("skipped").GetBoolean());
        Assert.True(response.GetProperty("locationId").GetInt32() > 0);
    }

    /// <summary>Fails only a new Location write, leaving any preceding visit saves untouched.</summary>
    private sealed class FailLocationWrite : SaveChangesInterceptor
    {
        /// <summary>Injects the demonstrated failure at the Location-only persistence boundary.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Location>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("Synthetic Location-write failure");
            return ValueTask.FromResult(result);
        }
    }
}
