using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Moq;
using Quartz;
using Quartz.Impl;
using Wayfarer.Jobs;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises explicit repair through committed Quartz metadata and the production job/worker.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    /// <summary>Only eligible future repair work keeps intent alive, and due entry needs no new command.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task TransientRepairContinuesThroughDueJobWithoutReschedulingWorkflowState()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedIncompleteRepairAsync(user.Id, protection);
        var factory = new FixtureDbContextFactory(fixture, []);
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
            { ["quartz.scheduler.instanceName"] = $"repair-{Guid.NewGuid():N}" }).GetScheduler();
        try
        {
            var projection = new WorkflowScheduleProjection(factory, new LocationEnrichmentScheduler(scheduler));
            await using (var stopped = fixture.CreateContext())
            {
                (await stopped.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == user.Id)).Cancel(DateTime.UtcNow);
                await stopped.SaveChangesAsync();
            }
            await using (var command = fixture.CreateContext())
                Assert.Equal("repair-scheduled", (await RepairCommandAsync(command, user.Id, projection)).Code);
            await using var inspect = fixture.CreateContext();
            var initial = await inspect.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            var key = LocationEnrichmentScheduler.TriggerKey(initial.SchedulerId, initial.Epoch);
            var trigger = Assert.IsAssignableFrom<ISimpleTrigger>(await scheduler.GetTrigger(key));
            Assert.Equal(initial.NextEligibleAtUtc, trigger.StartTimeUtc.UtcDateTime);
            Assert.Equal(0, trigger.RepeatCount);
            Assert.Equal(initial.Epoch, trigger.JobDataMap.GetInt("epoch"));
            var status = await RepairStatusAsync(inspect, user.Id);
            var progress = await new LocationEnrichmentProgressQuery(inspect).ProjectAsync(user.Id, status.Binding, DateTime.UtcNow);
            Assert.Equal(1, progress.RunnableRemaining);
            Assert.Equal(1, progress.IncompleteProviderAddresses);
            var failure = new CoordinatedHandler(user.Id, null, ContactOutcome.ProviderFailure);
            failure.Release();
            var worker = new LocationEnrichmentWorker(factory, new(factory), Service(protection, failure), projection);
            await ExecuteRepairJobAsync(user.Id, initial, worker);
            var waiting = await inspect.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            var attempt = await inspect.LocationEnrichmentAttempts.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Equal(LocationEnrichmentOutcome.RetryableFailure, attempt.Outcome);
            Assert.Equal(1, attempt.AdmittedAttemptCount);
            Assert.Null(attempt.OperationId);
            Assert.Equal(LocationEnrichmentState.BackingOff, waiting.State);
            Assert.True(waiting.IntentEnabled);
            Assert.Equal(attempt.NextAttemptAtUtc, waiting.NextEligibleAtUtc);
            Assert.True(waiting.NextEligibleAtUtc > DateTime.UtcNow.AddMinutes(4));
            trigger = Assert.IsAssignableFrom<ISimpleTrigger>(await scheduler.GetTrigger(key));
            Assert.Equal(waiting.NextEligibleAtUtc, trigger.StartTimeUtc.UtcDateTime);
            var success = new CoordinatedHandler(user.Id, null);
            success.Release();
            worker = new(factory, new(factory), Service(protection, success), projection);
            await ExecuteRepairJobAsync(user.Id, waiting, worker);
            Assert.Equal(0, success.RequestsFor(user.Id));
            Assert.Single(await inspect.GeoapifyUsageAdmissions.Where(x => x.UserId == user.Id).ToListAsync());
            progress = await new LocationEnrichmentProgressQuery(inspect).ProjectAsync(user.Id, status.Binding, DateTime.UtcNow);
            Assert.Equal((0, 1), (progress.RunnableRemaining, progress.FutureDue));
            Assert.Equal("Keep this address", (await inspect.Locations.SingleAsync(x => x.UserId == user.Id)).Address);
            // Advance only persisted due times; retain BackingOff, epoch, intent, and the real acquisition path.
            var due = DateTime.UtcNow.AddSeconds(-1);
            await inspect.LocationEnrichmentAttempts.Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, due));
            await inspect.LocationEnrichmentWorkflows.Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextEligibleAtUtc, due));
            await projection.ProjectAsync(user.Id);
            await ExecuteRepairJobAsync(user.Id, waiting, worker);
            var completed = await inspect.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Equal(LocationEnrichmentState.Completed, completed.State);
            Assert.Equal(initial.Epoch, completed.Epoch);
            Assert.False(completed.IntentEnabled);
            Assert.Equal(1, success.RequestsFor(user.Id));
            Assert.Equal(2, await inspect.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == user.Id));
            Assert.Equal("Alexandroupolis", (await inspect.Locations.AsNoTracking().SingleAsync(x => x.UserId == user.Id)).Place);
            progress = await new LocationEnrichmentProgressQuery(inspect).ProjectAsync(user.Id, status.Binding, DateTime.UtcNow);
            Assert.Equal((0, 0, 0), (progress.RunnableRemaining, progress.FutureDue, progress.IncompleteProviderAddresses));
            Assert.False(await scheduler.CheckExists(key));
        }
        finally { await scheduler.Shutdown(false); }
    }

    /// <summary>Fences a late cancelled contact even while a replacement repair owns the same attempt.</summary>
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrCancelDuringContactFencesEnrichmentAndRetainsAdmittedAttempt(bool cancel)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        if (cancel) await SeedIncompleteRepairAsync(user.Id, protection);
        else await SeedAsync(user.Id, null, protection);
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var workflow = cancel
                ? await setup.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == user.Id)
                : LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            if (!cancel) { workflow.Start(DateTime.UtcNow); setup.Add(workflow); }
            epoch = workflow.Epoch;
            await setup.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id, epoch);
        await handler.FirstUserRequestEntered;
        await using (var command = fixture.CreateContext())
        {
            var workflow = await command.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id);
            if (cancel) workflow.Cancel(DateTime.UtcNow); else workflow.Pause(DateTime.UtcNow);
            await command.SaveChangesAsync();
        }
        if (cancel)
        {
            // Cancel fences admission before any explicit restart and retains the already admitted usage.
            Assert.Equal(0, (await Service(protection, handler).RunAsync(user.Id, epoch)).Admitted);
            await using (var restart = fixture.CreateContext())
                Assert.Equal("repair-scheduled", (await RepairCommandAsync(restart, user.Id,
                    new Mock<IWorkflowScheduleProjection>().Object)).Code);
            var replacementHandler = new CoordinatedHandler(user.Id, null);
            var replacementRun = Service(protection, replacementHandler).RunAsync(user.Id);
            await replacementHandler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
            await using var inspect = fixture.CreateContext();
            var replacement = await inspect.LocationEnrichmentAttempts.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.NotNull(replacement.OperationId);
            Assert.True(replacement.OperationWorkflowEpoch > epoch);
            handler.Release();
            Assert.Equal(0, (await run).Succeeded);
            var retained = await inspect.LocationEnrichmentAttempts.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Equal(replacement.OperationId, retained.OperationId);
            Assert.Equal(replacement.OperationLeaseId, retained.OperationLeaseId);
            Assert.Equal(replacement.OperationWorkflowEpoch, retained.OperationWorkflowEpoch);
            var location = await inspect.Locations.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            Assert.Null(location.Place);
            Assert.Equal("Keep this address", location.Address);
            Assert.Equal(0, (await Service(protection, handler).RunAsync(user.Id, epoch)).Admitted);
            replacementHandler.Release();
            Assert.Equal(1, (await replacementRun).Succeeded);
            Assert.Equal(2, await inspect.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == user.Id));
            Assert.Empty(await inspect.LocationEnrichmentAttempts.Where(x => x.UserId == user.Id).ToListAsync());
            return;
        }
        handler.Release();
        await run;

        await using var verify = fixture.CreateContext();
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.NotNull(attempt.OperationId);
        Assert.NotNull(attempt.NextAttemptAtUtc);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
    }

    /// <summary>Uses projected identity data to enter the production job with a fresh relational context.</summary>
    private async Task ExecuteRepairJobAsync(string userId, LocationEnrichmentWorkflow workflow,
        ILocationEnrichmentWorker worker)
    {
        await using var db = fixture.CreateContext();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(x => x.MergedJobDataMap).Returns(new JobDataMap
        { ["schema"] = 1, ["workflowId"] = workflow.SchedulerId.ToString("N"), ["epoch"] = workflow.Epoch });
        await new LocationEnrichmentJob(db, worker).Execute(context.Object);
    }

    /// <summary>Reads test-owned authority; command locking still independently validates these bindings.</summary>
    private static async Task<PersonalProviderInspection> RepairStatusAsync(Wayfarer.Models.ApplicationDbContext db, string userId)
    {
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(x => x.UserId == userId);
        var selection = await db.PersonalLocationProviderSelections.SingleAsync(x => x.UserId == userId);
        return new(PersonalProviderAdmissionCategory.Admitted, "geoapify", true, false, null,
            new(0, 2500, "credits", null, null),
            new("geoapify", profile.Id, profile.CredentialGeneration, profile.GeocodingGeneration,
                selection.GeocodingSelectionGeneration, profile.GeocodingVerification,
                profile.GeocodingVerifiedCredentialGeneration, profile.GeocodingVerifiedConfigurationGeneration,
                null, null, null), DateTime.UtcNow);
    }

    private static async Task<EnrichmentCommandResult> RepairCommandAsync(Wayfarer.Models.ApplicationDbContext db,
        string userId, IWorkflowScheduleProjection projection)
    {
        var status = new Mock<IPersonalProviderStatusReader>();
        status.Setup(x => x.InspectPersistentGeocodingAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(await RepairStatusAsync(db, userId));
        return await new ImportEnrichmentHandoff(db, projection, status.Object,
            new LocationEnrichmentProgressQuery(db)).RepairIncompleteAsync(userId);
    }
}
