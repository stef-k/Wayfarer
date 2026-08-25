using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises import command races against the guarded relational concurrency token.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportLifecyclePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData("In Progress", false, false, false)]
    [InlineData("In Progress", true, false, false)]
    [InlineData("Stopping", true, true, false)]
    [InlineData("Stopped", false, false, false)]
    [InlineData("Stopped", false, true, true)]
    [InlineData("Completed", false, false, true)]
    [InlineData("Failed", false, false, false)]
    public async Task LifecycleConstraint_AcceptsEveryProductionCombination(
        string status, bool projectionPending, bool stopIntent, bool deletionIntent)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "LocationImports" SET "Status" = {status}, "ExecutionEpoch" = 0,
            "ProjectionPending" = {projectionPending},
            "StopRequestedAtUtc" = {stopIntent switch { true => DateTime.UtcNow, false => (DateTime?)null }},
            "DeletionRequestedAtUtc" = {deletionIntent switch { true => DateTime.UtcNow, false => (DateTime?)null }}
            WHERE "Id" = {seed.ImportId}
            """);
    }

    [PostgresTheory]
    [InlineData("Unknown", false, false, false)]
    [InlineData("In Progress", false, true, false)]
    [InlineData("In Progress", false, false, true)]
    [InlineData("Stopping", true, false, false)]
    [InlineData("Stopping", false, true, false)]
    [InlineData("Stopping", true, true, true)]
    [InlineData("Stopped", true, false, false)]
    [InlineData("Completed", false, true, false)]
    [InlineData("Failed", true, false, false)]
    public async Task LifecycleConstraint_RejectsMalformedAndUnknownCombinations(
        string status, bool projectionPending, bool stopIntent, bool deletionIntent)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "LocationImports" SET "Status" = {status}, "ProjectionPending" = {projectionPending},
            "StopRequestedAtUtc" = {stopIntent switch { true => DateTime.UtcNow, false => (DateTime?)null }},
            "DeletionRequestedAtUtc" = {deletionIntent switch { true => DateTime.UtcNow, false => (DateTime?)null }}
            WHERE "Id" = {seed.ImportId}
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [PostgresFact]
    public async Task ConcurrentStarts_ConvergeOnOneEpochAndProjection()
    {
        var seed = await SeedAsync();
        var (scheduler, schedules) = Scheduler();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        await Task.WhenAll(
            Owner(first, scheduler.Object).StartAsync(seed.UserId, seed.ImportId),
            Owner(second, scheduler.Object).StartAsync(seed.UserId, seed.ImportId));

        await using var verification = fixture.CreateContext();
        var stored = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.Equal(ImportStatus.InProgress, stored!.Status);
        Assert.Equal(1, stored.ExecutionEpoch);
        Assert.Single(schedules);
    }

    [PostgresFact]
    public async Task StopCommittedAfterStart_PreventsTerminalCompletionOverwrite()
    {
        var seed = await SeedAsync();
        var (scheduler, _) = Scheduler();
        await using (var start = fixture.CreateContext())
            await Owner(start, scheduler.Object).StartAsync(seed.UserId, seed.ImportId);
        await using (var stop = fixture.CreateContext())
            await Owner(stop, scheduler.Object).StopAsync(seed.UserId, seed.ImportId);
        await using (var worker = fixture.CreateContext())
            await Owner(worker, scheduler.Object).ConvergeExecutionAsync(seed.ImportId, 1, LocationImportExecutionOutcome.Completed);

        await using var verification = fixture.CreateContext();
        Assert.Equal(ImportStatus.Stopped, (await verification.LocationImports.FindAsync(seed.ImportId))!.Status);
    }

    [PostgresFact]
    public async Task StaleStopSave_ReturnsReloadedClassificationAndDoesNotReportFalseSuccess()
    {
        var seed = await SeedAsync();
        var (scheduler, _) = Scheduler();
        await using var staleStop = fixture.CreateContext();
        _ = await staleStop.LocationImports.FindAsync(seed.ImportId);

        await using (var start = fixture.CreateContext())
            Assert.True((await Owner(start, scheduler.Object).StartAsync(seed.UserId, seed.ImportId)).Succeeded);

        var result = await Owner(staleStop, scheduler.Object).StopAsync(seed.UserId, seed.ImportId);

        await using var verification = fixture.CreateContext();
        var stored = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.False(result.Succeeded);
        Assert.Equal(LocationImportCommandCode.InvalidState, result.Code);
        Assert.Equal(ImportStatus.InProgress, stored!.Status);
        Assert.Null(stored.StopRequestedAtUtc);
    }

    [PostgresFact]
    public async Task StaleDeleteIntent_ReturnsBoundedClassificationInsteadOfConcurrencyException()
    {
        var seed = await SeedAsync();
        var (scheduler, _) = Scheduler();
        await using var staleDelete = fixture.CreateContext();
        _ = await staleDelete.LocationImports.FindAsync(seed.ImportId);

        await using (var start = fixture.CreateContext())
            Assert.True((await Owner(start, scheduler.Object).StartAsync(seed.UserId, seed.ImportId)).Succeeded);

        var result = await Owner(staleDelete, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);

        Assert.Equal(LocationImportCommandCode.ExecutionActive, result.Code);
        await using var verification = fixture.CreateContext();
        Assert.Equal(ImportStatus.InProgress, (await verification.LocationImports.FindAsync(seed.ImportId))!.Status);
    }

    [PostgresFact]
    public async Task StaleStartSave_AfterLaterEpoch_DoesNotProjectObsoleteEpoch()
    {
        var seed = await SeedAsync();
        var (scheduler, schedules) = Scheduler();
        await using var staleStart = fixture.CreateContext();
        _ = await staleStart.LocationImports.FindAsync(seed.ImportId);
        await using (var first = fixture.CreateContext())
            await Owner(first, scheduler.Object).StartAsync(seed.UserId, seed.ImportId);
        await using (var completed = fixture.CreateContext())
            await Owner(completed, scheduler.Object).ConvergeExecutionAsync(
                seed.ImportId, 1, LocationImportExecutionOutcome.Completed);
        await using (var later = fixture.CreateContext())
            await Owner(later, scheduler.Object).StartAsync(seed.UserId, seed.ImportId);
        schedules.TryRemove(LocationImportSchedulerKeys.Job(seed.ImportId, 1), out _);

        var result = await Owner(staleStart, scheduler.Object).StartAsync(seed.UserId, seed.ImportId);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(LocationImportSchedulerKeys.Job(seed.ImportId, 1), schedules.Keys);
        Assert.Contains(LocationImportSchedulerKeys.Job(seed.ImportId, 2), schedules.Keys);
        await using var verification = fixture.CreateContext();
        Assert.Equal(2, (await verification.LocationImports.FindAsync(seed.ImportId))!.ExecutionEpoch);
    }

    [PostgresFact]
    public async Task ConcurrentStops_CommitOneIdempotentStopIntent()
    {
        var seed = await SeedAsync(ImportStatus.InProgress, epoch: 1);
        var (scheduler, _) = Scheduler();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var results = await Task.WhenAll(
            Owner(first, scheduler.Object).StopAsync(seed.UserId, seed.ImportId),
            Owner(second, scheduler.Object).StopAsync(seed.UserId, seed.ImportId));

        Assert.All(results, result => Assert.Contains(result.Code,
            new[] { LocationImportCommandCode.Accepted, LocationImportCommandCode.InvalidState }));
        await using var verification = fixture.CreateContext();
        var stored = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.Equal(ImportStatus.Stopping, stored!.Status);
        Assert.NotNull(stored.StopRequestedAtUtc);
    }

    [PostgresFact]
    public async Task CompletionCommittedBeforeStop_RemainsCompleted()
    {
        var seed = await SeedAsync(ImportStatus.InProgress, epoch: 1);
        var (scheduler, _) = Scheduler();
        await using (var worker = fixture.CreateContext())
            await Owner(worker, scheduler.Object).ConvergeExecutionAsync(
                seed.ImportId, 1, LocationImportExecutionOutcome.Completed);
        await using var stop = fixture.CreateContext();

        var result = await Owner(stop, scheduler.Object).StopAsync(seed.UserId, seed.ImportId);

        Assert.Equal(LocationImportCommandCode.InvalidState, result.Code);
        await using var verification = fixture.CreateContext();
        Assert.Equal(ImportStatus.Completed, (await verification.LocationImports.FindAsync(seed.ImportId))!.Status);
    }

    [PostgresFact]
    public async Task StaleWorkerTerminalWrite_AfterLaterStartCannotOverwriteCurrentEpoch()
    {
        var seed = await SeedAsync(ImportStatus.InProgress, epoch: 1);
        var (scheduler, _) = Scheduler();
        await using (var completed = fixture.CreateContext())
            await Owner(completed, scheduler.Object).ConvergeExecutionAsync(
                seed.ImportId, 1, LocationImportExecutionOutcome.Completed);
        await using (var restart = fixture.CreateContext())
            await Owner(restart, scheduler.Object).StartAsync(seed.UserId, seed.ImportId);
        await using (var stale = fixture.CreateContext())
            await Owner(stale, scheduler.Object).ConvergeExecutionAsync(
                seed.ImportId, 1, LocationImportExecutionOutcome.Failed);

        await using var verification = fixture.CreateContext();
        var stored = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.Equal(2, stored!.ExecutionEpoch);
        Assert.Equal(ImportStatus.InProgress, stored.Status);
        Assert.Null(stored.ErrorMessage);
    }

    [PostgresFact]
    public async Task ConcurrentTerminalDeletes_AreBoundedAndRemoveOnlyImportHistory()
    {
        var seed = await SeedAsync(ImportStatus.Completed, filePath: Path.GetTempFileName());
        var (scheduler, _) = Scheduler();
        scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync([]);
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var results = await Task.WhenAll(
            Owner(first, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId),
            Owner(second, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId));

        Assert.All(results, result => Assert.Contains(result.Code,
            new[] { LocationImportCommandCode.Accepted, LocationImportCommandCode.NotFound }));
        await using var verification = fixture.CreateContext();
        Assert.Null(await verification.LocationImports.FindAsync(seed.ImportId));
        Assert.NotNull(await verification.Users.FindAsync(seed.UserId));
    }

    [PostgresFact]
    public async Task QuartzDeleteFailure_RetainsDurableDeletionIntentForRestartRecovery()
    {
        var seed = await SeedAsync(ImportStatus.Completed, filePath: Path.GetTempFileName());
        var (scheduler, _) = Scheduler();
        scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync([LocationImportSchedulerKeys.Job(seed.ImportId, 1)]);
        scheduler.Setup(item => item.DeleteJob(It.IsAny<JobKey>(), default))
            .ThrowsAsync(new SchedulerException("fixture cleanup failure"));
        await using var db = fixture.CreateContext();

        var result = await Owner(db, scheduler.Object).DeleteAsync(seed.UserId, seed.ImportId);

        Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        await using var verification = fixture.CreateContext();
        var stored = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.NotNull(stored!.DeletionRequestedAtUtc);
        Assert.True(File.Exists(stored.FilePath));
        File.Delete(stored.FilePath);
    }

    [PostgresFact]
    public async Task CrossUserLifecycleCommandsRevealNoImportState()
    {
        var seed = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        var (scheduler, _) = Scheduler();
        await using var db = fixture.CreateContext();
        var owner = Owner(db, scheduler.Object);

        Assert.Equal(LocationImportCommandCode.NotFound,
            (await owner.StartAsync(other.Id, seed.ImportId)).Code);
        Assert.Equal(LocationImportCommandCode.NotFound,
            (await owner.StopAsync(other.Id, seed.ImportId)).Code);
        Assert.Equal(LocationImportCommandCode.NotFound,
            (await owner.DeleteAsync(other.Id, seed.ImportId)).Code);
    }

    private async Task<(string UserId, int ImportId)> SeedAsync(
        ImportStatus? status = null, int epoch = 0, string filePath = "guarded-upload")
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = filePath, FileType = LocationImportFileType.Csv,
            TotalRecords = 0, LastProcessedIndex = 0, Status = status ?? ImportStatus.Stopped,
            ExecutionEpoch = epoch,
            ProjectionPending = status?.Equals(ImportStatus.Stopping) == true,
            StopRequestedAtUtc = status?.Equals(ImportStatus.Stopping) == true ? DateTime.UtcNow : null
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return (user.Id, import.Id);
    }

    private LocationImportLifecycle Owner(ApplicationDbContext db, IScheduler scheduler)
        => new(new FixtureFactory(fixture), scheduler, NullLogger<LocationImportLifecycle>.Instance);

    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    private static (Mock<IScheduler> Scheduler, ConcurrentDictionary<JobKey, byte> Schedules) Scheduler()
    {
        var jobs = new ConcurrentDictionary<JobKey, byte>();
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobs.ContainsKey(key));
        scheduler.Setup(item => item.CheckExists(It.IsAny<TriggerKey>(), default)).ReturnsAsync(true);
        scheduler.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default))
            .ReturnsAsync((IJobDetail job, ITrigger _, CancellationToken _) =>
            {
                jobs.TryAdd(job.Key, 0);
                return DateTimeOffset.UtcNow;
            });
        scheduler.Setup(item => item.Interrupt(It.IsAny<JobKey>(), default)).ReturnsAsync(true);
        return (scheduler, jobs);
    }
}
