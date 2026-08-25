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
                await using var command = fixture.CreateContext();
                var import = await command.LocationImports.SingleAsync(x => x.Id == importId, token);
                import.Status = ImportStatus.Stopped;
                import.DeletionRequestedAtUtc = DateTime.UtcNow;
                await command.SaveChangesAsync(token);
                jobs.Add(job.Key);
                return DateTimeOffset.UtcNow;
            });
        scheduler.Setup(x => x.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobKey key, CancellationToken _) => jobs.Remove(key));

        await new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance).ReconcileAsync();

        Assert.Empty(jobs);
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
}
