using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves that a stale reconciliation snapshot cannot outlive deletion authority.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportFinalProjectionAuthorityTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(AuthorityChange.Stopped)]
    [InlineData(AuthorityChange.StopIntent)]
    [InlineData(AuthorityChange.DeletionIntent)]
    [InlineData(AuthorityChange.NewEpoch)]
    [InlineData(AuthorityChange.RowDeleted)]
    [InlineData(AuthorityChange.Completed)]
    [InlineData(AuthorityChange.Failed)]
    public async Task PostProjectionAuthorityChange_ConvergesInOneBoundedPass(AuthorityChange change)
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-matrix-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Latitude,Longitude");
        int importId;
        await using (var db = fixture.CreateContext())
        {
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 8, ProjectionPending = true,
                TotalRecords = 0, LastProcessedIndex = 0
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            importId = import.Id;
        }
        var jobs = new HashSet<JobKey>();
        var triggers = new HashSet<TriggerKey>();
        var changed = false;
        var scheduler = StatefulScheduler(jobs, triggers, async job =>
        {
            var expected = LocationImportSchedulerKeys.Job(importId, 8);
            if (changed || job.Key.Name != expected.Name || job.Key.Group != expected.Group) return;
            changed = true;
            await using var authority = fixture.CreateContext();
            var import = await authority.LocationImports.SingleAsync(x => x.Id == importId);
            switch (change)
            {
                case AuthorityChange.Stopped:
                    import.Status = ImportStatus.Stopped; import.ProjectionPending = false; break;
                case AuthorityChange.StopIntent:
                    import.Status = ImportStatus.Stopping; import.StopRequestedAtUtc = DateTime.UtcNow; break;
                case AuthorityChange.DeletionIntent:
                    import.Status = ImportStatus.Stopped; import.ProjectionPending = false;
                    import.DeletionRequestedAtUtc = DateTime.UtcNow; break;
                case AuthorityChange.NewEpoch:
                    import.ExecutionEpoch++; break;
                case AuthorityChange.RowDeleted:
                    authority.LocationImports.Remove(import); break;
                case AuthorityChange.Completed:
                    import.Status = ImportStatus.Completed; import.ProjectionPending = false; break;
                case AuthorityChange.Failed:
                    import.Status = ImportStatus.Failed; import.ProjectionPending = false; break;
            }
            await authority.SaveChangesAsync();
        });
        var reconciler = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance, new LocationImportProjectionCoordinator());

        await reconciler.ReconcileAsync();

        var matching = jobs.Where(key => LocationImportReconciler.TryParseJob(key, out var id, out _)
            && id == importId).ToHashSet();
        if (change == AuthorityChange.NewEpoch)
        {
            Assert.DoesNotContain(LocationImportSchedulerKeys.Job(importId, 8), matching);
            Assert.Equal([LocationImportSchedulerKeys.Job(importId, 9)], matching);
        }
        else Assert.Empty(matching);
        var mutations = scheduler.Invocations.Count(x => x.Method.Name is nameof(IScheduler.ScheduleJob)
            or nameof(IScheduler.DeleteJob));
        Assert.InRange(mutations, 1, 4);
        await reconciler.ReconcileAsync();
        Assert.Equal(mutations, scheduler.Invocations.Count(x => x.Method.Name is nameof(IScheduler.ScheduleJob)
            or nameof(IScheduler.DeleteJob)));
        await using (var cleanup = fixture.CreateContext())
            await cleanup.LocationImports.Where(x => x.Id == importId).ExecuteDeleteAsync();
        if (File.Exists(path)) File.Delete(path);
    }

    [PostgresFact]
    public async Task ReconcilerFirst_DeleteWaitsThenRemovesProjectionAndPhysicalImport()
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-reverse-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Latitude,Longitude");
        int importId;
        await using (var db = fixture.CreateContext())
        {
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 4, ProjectionPending = true,
                TotalRecords = 0, LastProcessedIndex = 0
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            importId = import.Id;
        }
        var jobs = new HashSet<JobKey>();
        var triggers = new HashSet<TriggerKey>();
        var projectionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = StatefulScheduler(jobs, triggers, async job =>
        {
            var expected = LocationImportSchedulerKeys.Job(importId, 4);
            if (job.Key.Name != expected.Name || job.Key.Group != expected.Group) return;
            projectionReached.TrySetResult();
            await releaseProjection.Task;
        });
        var coordinator = new LocationImportProjectionCoordinator();
        var contexts = new FixtureFactory(fixture);
        var reconciler = new LocationImportReconciler(contexts, scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance, coordinator);
        var lifecycle = new LocationImportLifecycle(contexts, scheduler.Object,
            NullLogger<LocationImportLifecycle>.Instance, coordinator);

        var reconciliation = reconciler.ReconcileAsync();
        await projectionReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var completed = fixture.CreateContext())
        {
            var import = await completed.LocationImports.SingleAsync(x => x.Id == importId);
            import.Status = ImportStatus.Completed;
            import.ProjectionPending = false;
            await completed.SaveChangesAsync();
        }
        var deletion = lifecycle.DeleteAsync(user.Id, importId);
        Assert.True(SpinWait.SpinUntil(() => coordinator.ReferenceCount(importId) == 2,
            TimeSpan.FromSeconds(10)));
        Assert.False(deletion.IsCompleted);
        Assert.True(File.Exists(path));
        await using (var blocked = fixture.CreateContext())
            Assert.NotNull(await blocked.LocationImports.FindAsync(importId));

        releaseProjection.TrySetResult();
        await Task.WhenAll(reconciliation, deletion);

        Assert.DoesNotContain(jobs, key => LocationImportReconciler.TryParseJob(key, out var id, out _)
            && id == importId);
        Assert.DoesNotContain(triggers, key => LocationImportReconciler.TryParseTrigger(key, out var id, out _)
            && id == importId);
        Assert.False(File.Exists(path));
        await using (var final = fixture.CreateContext())
            Assert.Null(await final.LocationImports.FindAsync(importId));
        var mutations = scheduler.Invocations.Count(x => x.Method.Name is nameof(IScheduler.ScheduleJob)
            or nameof(IScheduler.DeleteJob));
        await reconciler.ReconcileAsync();
        Assert.Equal(mutations, scheduler.Invocations.Count(x => x.Method.Name is nameof(IScheduler.ScheduleJob)
            or nameof(IScheduler.DeleteJob)));
        Assert.Equal(0, coordinator.EntryCount);
    }

    [PostgresFact]
    public async Task StaleReconcilerProjectionAfterDeletionIntent_DoesNotSurvive()
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-projection-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Latitude,Longitude");
        int importId;
        await using (var seed = fixture.CreateContext())
        {
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 6, ProjectionPending = true,
                TotalRecords = 0, LastProcessedIndex = 0
            };
            seed.LocationImports.Add(import);
            await seed.SaveChangesAsync();
            importId = import.Id;
        }

        var jobs = new HashSet<JobKey>();
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => jobs.ToHashSet());
        scheduler.Setup(x => x.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([]);
        scheduler.Setup(x => x.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IJobDetail job, ITrigger _, CancellationToken token) =>
            {
                if (job.Key.Name == LocationImportSchedulerKeys.Job(importId, 6).Name
                    && job.Key.Group == LocationImportSchedulerKeys.Group)
                {
                    await using var command = fixture.CreateContext();
                    var import = await command.LocationImports.SingleAsync(x => x.Id == importId, token);
                    import.Status = ImportStatus.Stopped;
                    import.ProjectionPending = false;
                    import.DeletionRequestedAtUtc = DateTime.UtcNow;
                    await command.SaveChangesAsync(token);
                }
                jobs.Add(job.Key);
                return DateTimeOffset.UtcNow;
            });
        scheduler.Setup(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobs.Remove(key));

        await new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance).ReconcileAsync();

        Assert.DoesNotContain(LocationImportSchedulerKeys.Job(importId, 6), jobs);
        await using var verification = fixture.CreateContext();
        Assert.Null(await verification.LocationImports.FindAsync(importId));
        Assert.False(File.Exists(path));
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken token = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static Mock<IScheduler> StatefulScheduler(HashSet<JobKey> jobs, HashSet<TriggerKey> triggers,
        Func<IJobDetail, Task> scheduled)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => jobs.ToHashSet());
        scheduler.Setup(x => x.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => triggers.ToHashSet());
        scheduler.Setup(x => x.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobs.Contains(key));
        scheduler.Setup(x => x.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TriggerKey key, CancellationToken _) => triggers.Contains(key));
        scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Returns(async (IJobDetail job, ITrigger trigger, CancellationToken _) =>
            {
                await scheduled(job);
                jobs.Add(job.Key);
                triggers.Add(trigger.Key);
                return DateTimeOffset.UtcNow;
            });
        scheduler.Setup(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) =>
            {
                triggers.RemoveWhere(trigger => trigger.Name.EndsWith(key.Name.Split('_')[^1], StringComparison.Ordinal));
                return jobs.Remove(key);
            });
        return scheduler;
    }

    public enum AuthorityChange { Stopped, StopIntent, DeletionIntent, NewEpoch, RowDeleted, Completed, Failed }
}
