using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Moq;
using Quartz;
using Quartz.Impl;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies initial/mid-batch exhaustion and deterministic native-meter rollover through existing Quartz jobs.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    [PostgresTheory(Timeout = 30_000)]
    [InlineData("geoapify", false)]
    [InlineData("geoapify", true)]
    [InlineData("mapbox", false)]
    [InlineData("mapbox", true)]
    public async Task ImportOptInWaitsForBudgetAndDueJobContinuesAfterNativeMeterReset(string provider, bool midRun)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        if (provider == "mapbox") await SeedMapboxAsync(user.Id, protection);
        else await SeedAsync(user.Id, null, protection);
        await ConfigureBudgetAsync(user.Id, provider, midRun);
        var factory = new FixtureDbContextFactory(fixture, []);
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
            { ["quartz.scheduler.instanceName"] = $"import-budget-{Guid.NewGuid():N}" }).GetScheduler();
        try
        {
            var projection = new WorkflowScheduleProjection(factory, new(scheduler));
            await using var command = fixture.CreateContext();
            var handoff = new ImportEnrichmentHandoff(command, projection, Status(factory, protection), new LocationEnrichmentProgressQuery(command));
            Assert.Equal(EnrichmentHandoffDisposition.ContinuationAccepted, await handoff.EnsureAsync(user.Id));
            var handler = new BudgetSuccessHandler();
            var worker = new LocationEnrichmentWorker(factory, new(factory), Service(protection, handler), projection);
            var initial = await command.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            if (midRun) await worker.RunBatchAsync(user.Id, initial.Epoch, default);
            var waiting = await command.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Equal(LocationEnrichmentState.PausedByBudget, waiting.State);
            Assert.Equal(LocationEnrichmentOutcome.BudgetExhausted, waiting.Outcome);
            Assert.True(waiting.IntentEnabled);
            var inspection = await Status(factory, protection).InspectPersistentGeocodingAsync(user.Id);
            Assert.True(inspection.Exhausted);
            Assert.NotNull(inspection.Binding);
            var expectedWake = provider == "mapbox" ? LocationEnrichmentRetryPolicy.MapboxWake(new(inspection.DatabaseNowUtc))
                : inspection.NextAvailableAt!.Value;
            Assert.Equal(expectedWake.UtcDateTime, waiting.NextEligibleAtUtc);
            var key = LocationEnrichmentScheduler.TriggerKey(waiting.SchedulerId, waiting.Epoch);
            var trigger = Assert.IsAssignableFrom<ISimpleTrigger>(await scheduler.GetTrigger(key));
            Assert.InRange(trigger.StartTimeUtc.UtcDateTime - waiting.NextEligibleAtUtc!.Value, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
            Assert.Equal(EnrichmentHandoffDisposition.ContinuationAccepted, await handoff.EnsureAsync(user.Id));
            Assert.Equal(waiting.NextEligibleAtUtc,
                (await command.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id)).NextEligibleAtUtc);
            await worker.RunBatchAsync(user.Id, waiting.Epoch, default);
            Assert.Equal(midRun ? 1 : 0, handler.Requests);
            await ResetBudgetAndMakeWakeDueAsync(user.Id, provider);
            await projection.ProjectAsync(user.Id);
            scheduler.JobFactory = new LocationEnrichmentQuartzPostgresTests.ProductionJobFactory(fixture, worker);
            await scheduler.Start();
            await LocationEnrichmentQuartzPostgresTests.WaitUntilAsync(async () =>
                await command.LocationEnrichmentWorkflows.AsNoTracking().AnyAsync(x => x.UserId == user.Id
                    && x.State == LocationEnrichmentState.Completed), TimeSpan.FromSeconds(5));
            await LocationEnrichmentQuartzPostgresTests.WaitUntilAsync(async () =>
                (await scheduler.GetCurrentlyExecutingJobs()).Count == 0, TimeSpan.FromSeconds(5));
            Assert.Equal(midRun ? 2 : 1, handler.Requests);
            Assert.Equal(midRun ? 2 : 1, await command.Locations.CountAsync(x => x.UserId == user.Id && x.ReverseGeocodingProvider == provider));
            var finished = await command.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Equal(initial.Epoch, finished.Epoch);
            Assert.Equal(midRun ? 2 : 1, finished.EnrichedCount);
            // Existing Geoapify admission prunes expired rolling-window rows; the current charged admission remains.
            if (provider == "geoapify") Assert.Equal(1, await command.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == user.Id));
            else Assert.Equal(1, (await command.MapboxProductMeters.AsNoTracking().SingleAsync(x => x.UserId == user.Id)).AdmittedCount);
        }
        finally { await scheduler.Shutdown(true); }
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrCancelSurvivesDelayedHandoffMissingCredentialsResetAndReconstruction(bool cancel)
    {
        await using var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(scope.User.Id, null, protection);
        await ConfigureBudgetAsync(scope.User.Id, "geoapify", false);
        var projection = new Mock<IWorkflowScheduleProjection>();
        await using (var first = fixture.CreateContext())
        {
            var handoff = new ImportEnrichmentHandoff(first, projection.Object, Status(scope.Factory, protection), new LocationEnrichmentProgressQuery(first));
            await handoff.EnsureAsync(scope.User.Id);
            if (cancel) await handoff.CancelAsync(scope.User.Id); else await handoff.PauseAsync(scope.User.Id);
        }
        string savedCredential;
        await using (var revoke = fixture.CreateContext())
        {
            var profile = await revoke.PersonalLocationProviderProfiles.SingleAsync(x => x.UserId == scope.User.Id);
            savedCredential = profile.StableProtectedCredential!;
            profile.StableProtectedCredential = null;
            await revoke.SaveChangesAsync();
        }
        await using (var reconstructed = fixture.CreateContext())
            Assert.Equal(EnrichmentHandoffDisposition.Held, await new ImportEnrichmentHandoff(reconstructed,
                projection.Object, Status(scope.Factory, protection), new LocationEnrichmentProgressQuery(reconstructed)).EnsureAsync(scope.User.Id));
        await ResetBudgetAndMakeWakeDueAsync(scope.User.Id, "geoapify", advanceWake: false);
        await using var afterReset = fixture.CreateContext();
        var owner = new ImportEnrichmentHandoff(afterReset, projection.Object, Status(scope.Factory, protection), new LocationEnrichmentProgressQuery(afterReset));
        Assert.Equal(EnrichmentHandoffDisposition.Held, await owner.EnsureAsync(scope.User.Id));
        var held = await afterReset.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == scope.User.Id);
        Assert.Equal(cancel ? LocationEnrichmentState.Cancelled : LocationEnrichmentState.PausedByUser, held.State);
        Assert.False(held.IntentEnabled);
        Assert.Null(held.NextEligibleAtUtc);
        Assert.Equal(1, await afterReset.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == scope.User.Id));
        var authorityMissing = await owner.ControlAsync(scope.User.Id, cancel ? EnrichmentControl.Start : EnrichmentControl.Resume,
            new(held.Epoch, held.State, scope.Credential));
        Assert.Equal(LocationEnrichmentCommandResult.AuthorityUnavailable, authorityMissing.Classification);
        await using (var repair = fixture.CreateContext())
        {
            var profile = await repair.PersonalLocationProviderProfiles.SingleAsync(x => x.UserId == scope.User.Id);
            profile.StableProtectedCredential = savedCredential;
            await repair.SaveChangesAsync();
        }
        Assert.Equal(EnrichmentHandoffDisposition.Held, await owner.EnsureAsync(scope.User.Id));
        Assert.Equal(LocationEnrichmentCommandResult.Applied, (await owner.ControlAsync(scope.User.Id,
            cancel ? EnrichmentControl.Start : EnrichmentControl.Resume, new(held.Epoch, held.State, scope.Credential))).Classification);
    }

    private async Task ConfigureBudgetAsync(string userId, string provider, bool midRun)
    {
        await using var db = fixture.CreateContext();
        if (provider == "geoapify")
        {
            (await db.GeoapifyUsageGuards.SingleAsync(x => x.UserId == userId)).CreditLimit = 1;
            if (!midRun) db.Add(new GeoapifyUsageAdmission { UserId = userId, Credits = 1,
                Product = PersonalProviderProduct.Routing, AdmittedAt = DateTimeOffset.UtcNow.AddHours(-23) });
        }
        else db.Add(new MapboxProductMeter { UserId = userId, Limit = 1, Product = PersonalProviderProduct.PermanentGeocoding,
            CycleStart = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), AdmittedCount = midRun ? 0 : 1 });
        if (midRun) db.Add(new Location { UserId = userId, Timestamp = DateTime.UtcNow.AddMinutes(1),
            LocalTimestamp = DateTime.UtcNow.AddMinutes(1), TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(23, 37) { SRID = 4326 } });
        await db.SaveChangesAsync();
    }

    /// <summary>Rebases only fixture-owned persisted clocks/meters; acquisition, admission and Quartz dispatch stay real.</summary>
    private async Task ResetBudgetAndMakeWakeDueAsync(string userId, string provider, bool advanceWake = true)
    {
        await using var db = fixture.CreateContext();
        if (provider == "geoapify") await db.GeoapifyUsageAdmissions.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.AdmittedAt, DateTimeOffset.UtcNow.AddHours(-25)));
        else await db.MapboxProductMeters.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.CycleStart, DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1)));
        if (advanceWake) await db.LocationEnrichmentWorkflows.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.NextEligibleAtUtc, DateTime.UtcNow.AddSeconds(-1)));
    }

    /// <summary>Returns fixed local provider fixtures without any live or paid-provider contact.</summary>
    private sealed class BudgetSuccessHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var json = request.RequestUri!.Host.Contains("mapbox", StringComparison.Ordinal)
                ? """{"type":"FeatureCollection","features":[{"type":"Feature","id":"controlled","geometry":{"type":"Point","coordinates":[23,37]},"properties":{"feature_type":"street","full_address":"Controlled address","name":"Main Street","context":{"place":{"name":"Athens"},"country":{"name":"Greece"}}}}]}"""
                : """{"type":"FeatureCollection","features":[{"properties":{"formatted":"Address","address_line1":"Address","city":"Athens","result_type":"building"}}]}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
