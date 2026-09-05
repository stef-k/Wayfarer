using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NetTopologySuite.Geometries;
using System.Data.Common;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the cancelled-state command sets independently using the atomic command fixture.</summary>
public sealed partial class LocationEnrichmentRetryAtomicityPostgresTests
{
    /// <summary>Each explicit restart commits only its distinct set with a fresh epoch.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledReportedStateRestartsOnlyRequestedSet(bool repair)
    {
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        var binding = (await scenario.Status.Object.InspectPersistentGeocodingAsync(scenario.UserId)).Binding!;
        await using var db = fixture.CreateContext();
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scenario.UserId);
        workflow.Cancel(DateTime.UtcNow);
        var epoch = workflow.Epoch;
        var second = await db.Locations.SingleAsync(x => x.UserId == scenario.UserId
            && !db.LocationEnrichmentAttempts.Any(a => a.LocationId == x.Id));
        var deferred = new LocationEnrichmentAttempt { UserId = scenario.UserId, LocationId = second.Id };
        deferred.PrepareRepair(binding, DateTime.UtcNow);
        deferred.Outcome = LocationEnrichmentOutcome.NoResult;
        deferred.AdmittedAttemptCount = 1;
        db.Add(deferred);
        var user = await db.Users.SingleAsync(x => x.Id == scenario.UserId);
        for (var i = 0; i < 11; i++)
        {
            var location = TestDataFixtures.CreateLocation(user);
            location.Address = "Retain address";
            location.ReverseGeocodingProvider = "geoapify";
            location.ReverseGeocodingStorageMode = "persistent";
            location.ReverseGeocodedAt = DateTimeOffset.UtcNow;
            db.Add(location);
        }
        await db.SaveChangesAsync();
        var progress = await new LocationEnrichmentProgressQuery(db).ProjectAsync(scenario.UserId, binding, DateTime.UtcNow);
        Assert.Equal((0, 2, 11), (progress.RunnableRemaining, progress.ManualRetryAvailable, progress.IncompleteProviderAddresses));
        scenario.Projection.Setup(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var committed = fixture.CreateContext();
                var saved = await committed.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scenario.UserId);
                Assert.Equal(epoch + 1, saved.Epoch);
                Assert.Equal(LocationEnrichmentState.Scheduled, saved.State);
                Assert.True(saved.IntentEnabled);
                Assert.NotNull(saved.NextEligibleAtUtc);
                Assert.Equal(repair ? 11 : 2, await committed.LocationEnrichmentAttempts.CountAsync(
                    x => x.UserId == scenario.UserId && x.Outcome == LocationEnrichmentOutcome.None));
            });
        var command = Command(scenario, db);
        var result = repair ? await command.RepairIncompleteAsync(scenario.UserId)
            : await command.RetryDeferredAsync(scenario.UserId);
        Assert.Equal(repair ? "repair-scheduled" : "scheduled", result.Code);
        await using var verify = fixture.CreateContext();
        Assert.Equal(repair ? 2 : 0, await verify.LocationEnrichmentAttempts.CountAsync(
            x => x.UserId == scenario.UserId && x.Outcome == LocationEnrichmentOutcome.NoResult));
        Assert.Equal(repair ? 13 : 2, await verify.LocationEnrichmentAttempts.CountAsync(x => x.UserId == scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Start consumes prepared work but never creates repair intent for an unprepared partial address.</summary>
    [PostgresFact]
    public async Task CancelledStartUsesPreparedRepairWithoutPreparingAnotherAddress()
    {
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        var binding = (await scenario.Status.Object.InspectPersistentGeocodingAsync(scenario.UserId)).Binding!;
        await using var db = fixture.CreateContext();
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scenario.UserId);
        workflow.Cancel(DateTime.UtcNow);
        var epoch = workflow.Epoch;
        var location = await db.Locations.SingleAsync(x => x.UserId == scenario.UserId
            && !db.LocationEnrichmentAttempts.Any(a => a.LocationId == x.Id));
        location.Address = "Preserve address";
        location.ReverseGeocodingProvider = "geoapify";
        location.ReverseGeocodingStorageMode = "persistent";
        location.ReverseGeocodedAt = DateTimeOffset.UtcNow;
        var prepared = new LocationEnrichmentAttempt { UserId = scenario.UserId, LocationId = location.Id };
        prepared.PrepareRepair(binding, DateTime.UtcNow);
        db.Add(prepared);
        var user = await db.Users.SingleAsync(x => x.Id == scenario.UserId);
        var unprepared = TestDataFixtures.CreateLocation(user);
        unprepared.Address = "Unprepared address";
        unprepared.ReverseGeocodingProvider = "geoapify";
        unprepared.ReverseGeocodingStorageMode = "persistent";
        unprepared.ReverseGeocodedAt = DateTimeOffset.UtcNow;
        db.Add(unprepared);
        await db.SaveChangesAsync();
        Assert.Equal("scheduled", (await Command(scenario, db).StartAsync(scenario.UserId)).Code);
        Assert.Equal(epoch + 1, workflow.Epoch);
        Assert.True(workflow.IntentEnabled);
        Assert.Equal(LocationEnrichmentState.Scheduled, workflow.State);
        Assert.False(await db.LocationEnrichmentAttempts.AnyAsync(x => x.LocationId == unprepared.Id));
        Assert.Equal(1, await db.LocationEnrichmentAttempts.CountAsync(x => x.UserId == scenario.UserId
            && x.Outcome == LocationEnrichmentOutcome.NoResult));
    }
}
