using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    private async Task<(string UserId, int ImportId)> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = "guarded-upload", FileType = LocationImportFileType.Csv,
            TotalRecords = 0, LastProcessedIndex = 0, Status = ImportStatus.Stopped
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return (user.Id, import.Id);
    }

    private static LocationImportLifecycle Owner(ApplicationDbContext db, IScheduler scheduler)
        => new(db, scheduler, NullLogger<LocationImportLifecycle>.Instance);

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
