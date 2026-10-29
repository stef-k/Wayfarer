using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz.Impl;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Uses real import, backfill and workflow owners to reproduce the final-empty-scan lost-work boundary.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    [PostgresFact(Timeout = 30_000)]
    public async Task RunningEmptyScanRetainsImportedCheckpointThroughFileRemovalUntilMaintenanceContinues()
    {
        await using var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(scope.User.Id, null, protection);
        await using (var setup = fixture.CreateContext())
        {
            var existing = await setup.Locations.SingleAsync(x => x.UserId == scope.User.Id);
            existing.Address = "Keep the manual address";
            var workflow = LocationEnrichmentWorkflow.Create(scope.User.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            setup.Add(workflow);
            await setup.SaveChangesAsync();
        }
        var row = await scope.AcceptAsync(enrichmentRequested: true);
        var handler = new CoordinatedHandler(scope.User.Id, null);
        handler.Release();
        var batch = Service(protection, handler);
        var gate = new EmptyScanGate(batch);
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
            { ["quartz.scheduler.instanceName"] = $"import-handoff-{Guid.NewGuid():N}" }).GetScheduler();
        try
        {
            var projection = new WorkflowScheduleProjection(scope.Factory, new(scheduler));
            var worker = new LocationEnrichmentWorker(scope.Factory, new(scope.Factory), gate, projection);
            var oldRun = worker.RunBatchAsync(scope.User.Id, 1, default);
            await gate.Scanned.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await using var commandDb = fixture.CreateContext();
                var handoff = new ImportEnrichmentHandoff(commandDb, projection, Status(scope.Factory, protection),
                    new LocationEnrichmentProgressQuery(commandDb));
                await scope.Lifecycle.StartAsync(scope.User.Id, row.ImportId!.Value, 1, scope.Credential, default);
                var importer = new LocationImportService(scope.Files, scope.Factory,
                    new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                    NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService(), handoff);
                await importer.ProcessImport(row.ImportId.Value, 1, default);
                var completedImport = (await scope.ReadAsync(row.Id)).Import!;
                Assert.Equal(ImportStatus.Completed, completedImport.Status);
                Assert.Equal(1, completedImport.LastProcessedIndex);
                Assert.True(completedImport.EnrichmentHandoffPending);
                Assert.Equal(EnrichmentHandoffDisposition.Pending, await handoff.EnsureAsync(scope.User.Id));
                await scope.DueAsync(row);
                await scope.Cleanup.ProcessAsync(row.Id, default);
                Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
                Assert.False(await scope.Lifecycle.DeleteAsync(scope.User.Id, row.ImportId.Value, scope.Credential, default));
                Assert.True((await scope.ReadAsync(row.Id)).Import!.EnrichmentHandoffPending);
            }
            finally { gate.Release(); await oldRun; }
            await using var recoveryDb = fixture.CreateContext();
            Assert.Equal(LocationEnrichmentState.Completed,
                (await recoveryDb.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == scope.User.Id)).State);
            var recovered = new ImportEnrichmentHandoff(recoveryDb, projection, Status(scope.Factory, protection),
                new LocationEnrichmentProgressQuery(recoveryDb));
            await new LocationEnrichmentReconciler(scope.Factory, new(scheduler), scheduler).ReconcileAsync();
            await new LocationImportEnrichmentHandoff(scope.Factory, recovered).RetryPendingAsync(0);
            Assert.False((await scope.ReadAsync(row.Id)).Import!.EnrichmentHandoffPending);
            var continuation = await recoveryDb.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == scope.User.Id);
            Assert.Equal(LocationEnrichmentState.Scheduled, continuation.State);
            Assert.True(await scheduler.CheckExists(LocationEnrichmentScheduler.TriggerKey(continuation.SchedulerId, continuation.Epoch)));
            await new LocationEnrichmentWorker(scope.Factory, new(scope.Factory), batch, projection)
                .RunBatchAsync(scope.User.Id, continuation.Epoch, default);
            Assert.Equal(1, handler.RequestsFor(scope.User.Id));
            Assert.Equal(1, await recoveryDb.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == scope.User.Id));
            Assert.True(await recoveryDb.Locations.AnyAsync(x => x.UserId == scope.User.Id && x.Address == "Keep the manual address"));
            Assert.True(await recoveryDb.Locations.AnyAsync(x => x.UserId == scope.User.Id && x.Place == "Alexandroupolis"));
            Assert.True(await scope.Lifecycle.DeleteAsync(scope.User.Id, row.ImportId!.Value, scope.Credential, default));
        }
        finally { await scheduler.Shutdown(true); }
    }

    [PostgresFact]
    public async Task AcceptedEarlierCheckpointCannotClearLaterCommittedBatch()
    {
        await using var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(scope.User.Id, null, protection);
        var row = await scope.AcceptAsync(enrichmentRequested: true);
        await using (var setup = fixture.CreateContext())
        {
            var import = await setup.LocationImports.SingleAsync(x => x.Id == row.ImportId);
            import.LastProcessedIndex = 1;
            import.EnrichmentHandoffPending = true;
            await setup.SaveChangesAsync();
        }
        var projection = new Mock<IWorkflowScheduleProjection>();
        var laterCommitted = false;
        projection.Setup(x => x.ProjectAsync(scope.User.Id, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (laterCommitted) return;
            await using var later = fixture.CreateContext();
            var import = await later.LocationImports.SingleAsync(x => x.Id == row.ImportId);
            import.LastProcessedIndex = 2;
            import.EnrichmentHandoffPending = true;
            await later.SaveChangesAsync();
            laterCommitted = true;
        });
        await using var command = fixture.CreateContext();
        var owner = new ImportEnrichmentHandoff(command, projection.Object, Status(scope.Factory, protection),
            new LocationEnrichmentProgressQuery(command));
        var pending = new LocationImportEnrichmentHandoff(scope.Factory, owner);
        await pending.ReconcileAsync(row.ImportId!.Value);
        Assert.True((await scope.ReadAsync(row.Id)).Import!.EnrichmentHandoffPending);
        await pending.ReconcileAsync(row.ImportId.Value);
        Assert.False((await scope.ReadAsync(row.Id)).Import!.EnrichmentHandoffPending);
    }

    private static PersonalProviderStatusReader Status(Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> factory,
        IDataProtectionProvider protection) => new(factory, CredentialTestFactory.Create(protection), new ConfigurationBuilder().Build());

    /// <summary>Pauses after the real batch's empty final scan while the real worker still owns Running.</summary>
    private sealed class EmptyScanGate(ILocationEnrichmentBatch batch) : ILocationEnrichmentBatch
    {
        private readonly TaskCompletionSource scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Scanned => scanned.Task;
        public void Release() => release.TrySetResult();
        public async Task<GeoapifyBackfillResult> RunAsync(LocationEnrichmentExecutionLease owner, CancellationToken token = default)
        {
            var result = await batch.RunAsync(owner, token);
            Assert.Equal(0, result.RemainingEstimate);
            scanned.TrySetResult();
            await release.Task.WaitAsync(token);
            return result;
        }
    }
}
