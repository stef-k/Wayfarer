using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Jobs;

/// <summary>
/// Background job behaviors: cleanup, imports, and listener logging.
/// </summary>
public class JobTests : TestBase
{
    [Fact]
    public async Task LogCleanupJob_RemovesFilesOlderThanMonth()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wf-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var oldFile = Path.Combine(tempDir, "wayfarer-old.log");
        var recentFile = Path.Combine(tempDir, "wayfarer-recent.log");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(recentFile, "recent");
        File.SetCreationTime(oldFile, DateTime.Now.AddMonths(-2));
        File.SetCreationTime(recentFile, DateTime.Now);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogFilePath:Default"] = Path.Combine(tempDir, "wayfarer-current.log")
            })
            .Build();

        var job = new LogCleanupJob(config, NullLogger<LogCleanupJob>.Instance);
        var jobDetail = JobBuilder.Create<LogCleanupJob>().WithIdentity("logCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        try
        {
            await job.Execute(context.Object);

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(recentFile));
            Assert.Equal("Completed", jobDetail.JobDataMap["Status"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditLogCleanupJob_RemovesLogsOlderThanTwoYears()
    {
        var db = CreateDbContext();
        db.AuditLogs.Add(new AuditLog
        {
            UserId = "old",
            Action = "OldAction",
            Timestamp = DateTime.UtcNow.AddYears(-3),
            Details = "old"
        });
        db.AuditLogs.Add(new AuditLog
        {
            UserId = "recent",
            Action = "RecentAction",
            Timestamp = DateTime.UtcNow.AddMonths(-1),
            Details = "recent"
        });
        await db.SaveChangesAsync();

        var jobDetail = JobBuilder.Create<AuditLogCleanupJob>().WithIdentity("auditCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        var job = new AuditLogCleanupJob(db);
        await job.Execute(context.Object);

        var remaining = Assert.Single(db.AuditLogs);
        Assert.Equal("recent", remaining.UserId);
        Assert.Equal("Completed", jobDetail.JobDataMap["Status"]);
    }

    [Fact]
    public async Task AuditLogCleanupJob_RespectsCanellationToken()
    {
        var db = CreateDbContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var jobDetail = JobBuilder.Create<AuditLogCleanupJob>().WithIdentity("auditCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(cts.Token);
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        var job = new AuditLogCleanupJob(db);

        await Assert.ThrowsAsync<OperationCanceledException>(() => job.Execute(context.Object));
        Assert.Equal("Cancelled", jobDetail.JobDataMap["Status"]);
    }

    [Fact]
    public async Task LogCleanupJob_RespectsCanellationToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wf-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogFilePath:Default"] = Path.Combine(tempDir, "wayfarer-current.log")
            })
            .Build();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var jobDetail = JobBuilder.Create<LogCleanupJob>().WithIdentity("logCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);
        context.SetupGet(c => c.CancellationToken).Returns(cts.Token);

        var job = new LogCleanupJob(config, NullLogger<LogCleanupJob>.Instance);

        try
        {
            await job.Execute(context.Object);

            Assert.Equal("Cancelled", jobDetail.JobDataMap["Status"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LocationImportJob_InvokesServiceWithImportId()
    {
        var service = new Mock<ILocationImportService>();
        var job = new LocationImportJob(service.Object, NullLogger<LocationImportJob>.Instance);
        var jobDetail = JobBuilder.Create<LocationImportJob>()
            .WithIdentity("locImport", "tests")
            .UsingJobData("importId", 123)
            .Build();
        var tokenSource = new CancellationTokenSource();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);
        context.SetupGet(c => c.CancellationToken).Returns(tokenSource.Token);

        await job.Execute(context.Object);

        service.Verify(s => s.ProcessImport(123, tokenSource.Token), Times.Once);
    }

    [Fact]
    public async Task JobExecutionListener_WritesJobHistory_OnCompletion()
    {
        var db = CreateDbContext();
        var sseService = new SseService();
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(db);
        providerMock.Setup(p => p.GetService(typeof(SseService))).Returns(sseService);
        var scope = Mock.Of<IServiceScope>(s => s.ServiceProvider == providerMock.Object);
        var scopeFactory = Mock.Of<IServiceScopeFactory>(f => f.CreateScope() == scope);
        var listener = new JobExecutionListener(scopeFactory);

        var jobDetail = JobBuilder.Create<LocationImportJob>().WithIdentity("importJob", "jobs").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        await listener.JobWasExecuted(context.Object, jobException: null, CancellationToken.None);

        var history = Assert.Single(db.JobHistories);
        Assert.Equal("importJob", history.JobName);
        Assert.Equal("Completed", history.Status);
        Assert.True(history.LastRunTime.HasValue);
    }

    #region VisitCleanupJob Tests

    [Fact]
    public async Task VisitCleanupJob_ClosesStaleOpenVisits()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Add settings with VisitedEndVisitAfterMinutes = 45 (via LocationTimeThresholdMinutes = 5)
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5 // VisitedEndVisitAfterMinutes = 5 * 9 = 45 min
        });

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Stale open visit - LastSeenAtUtc over 45 min ago
        var staleVisit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-2),
            LastSeenAtUtc = DateTime.UtcNow.AddHours(-1), // 60 min ago > 45 min threshold
            EndedAtUtc = null, // Open
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Trip",
            RegionNameSnapshot = "Region",
            PlaceNameSnapshot = "Stale Place"
        };

        // Recent open visit - LastSeenAtUtc within 45 min
        var recentVisit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-10), // 10 min ago < 45 min threshold
            EndedAtUtc = null, // Open
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Trip",
            RegionNameSnapshot = "Region",
            PlaceNameSnapshot = "Recent Place"
        };

        db.PlaceVisitEvents.AddRange(staleVisit, recentVisit);
        await db.SaveChangesAsync();

        var settingsService = new ApplicationSettingsService(db, cache);
        var job = new VisitCleanupJob(db, settingsService, NullLogger<VisitCleanupJob>.Instance);

        var jobDetail = JobBuilder.Create<VisitCleanupJob>().WithIdentity("visitCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        // Act
        await job.Execute(context.Object);

        // Assert
        var staleResult = await db.PlaceVisitEvents.FindAsync(staleVisit.Id);
        var recentResult = await db.PlaceVisitEvents.FindAsync(recentVisit.Id);

        Assert.NotNull(staleResult!.EndedAtUtc); // Should be closed
        Assert.Equal(staleVisit.LastSeenAtUtc, staleResult.EndedAtUtc); // EndedAtUtc = LastSeenAtUtc
        Assert.Null(recentResult!.EndedAtUtc); // Should still be open
        Assert.Equal("Completed", jobDetail.JobDataMap["Status"]);
    }

    [Fact]
    public async Task VisitCleanupJob_DeletesStaleCandidates()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Add settings with VisitedCandidateStaleMinutes = 60 (via LocationTimeThresholdMinutes = 5)
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5 // VisitedCandidateStaleMinutes = 5 * 12 = 60 min
        });

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Test Trip",
            UserId = user.Id,
            IsPublic = false
        };
        db.Trips.Add(trip);

        var region = new Region { Id = Guid.NewGuid(), Name = "Region", TripId = trip.Id };
        db.Regions.Add(region);

        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = "Test Place",
            RegionId = region.Id,
            Location = new Point(23.72, 37.97) { SRID = 4326 }
        };
        db.Places.Add(place);

        // Stale candidate - LastHitUtc over 60 min ago
        var staleCandidate = new PlaceVisitCandidate
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow.AddHours(-2),
            LastHitUtc = DateTime.UtcNow.AddMinutes(-90), // 90 min ago > 60 min threshold
            ConsecutiveHits = 1
        };

        // Recent candidate - LastHitUtc within 60 min
        var recentCandidate = new PlaceVisitCandidate
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow.AddMinutes(-20),
            LastHitUtc = DateTime.UtcNow.AddMinutes(-10), // 10 min ago < 60 min threshold
            ConsecutiveHits = 1
        };

        // Need unique constraint workaround - use different places
        var place2 = new Place
        {
            Id = Guid.NewGuid(),
            Name = "Test Place 2",
            RegionId = region.Id,
            Location = new Point(23.73, 37.98) { SRID = 4326 }
        };
        db.Places.Add(place2);
        recentCandidate.PlaceId = place2.Id;

        db.PlaceVisitCandidates.AddRange(staleCandidate, recentCandidate);
        await db.SaveChangesAsync();

        var settingsService = new ApplicationSettingsService(db, cache);
        var job = new VisitCleanupJob(db, settingsService, NullLogger<VisitCleanupJob>.Instance);

        var jobDetail = JobBuilder.Create<VisitCleanupJob>().WithIdentity("visitCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        // Act
        await job.Execute(context.Object);

        // Assert
        var candidates = db.PlaceVisitCandidates.ToList();
        Assert.Single(candidates);
        Assert.Equal(recentCandidate.Id, candidates[0].Id); // Only recent remains
        Assert.Equal("Completed", jobDetail.JobDataMap["Status"]);
    }

    [Fact]
    public async Task VisitCleanupJob_ReadsSettingsDynamically()
    {
        // Arrange
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Add settings with different threshold
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 10 // VisitedEndVisitAfterMinutes = 10 * 9 = 90 min
        });

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Visit that is 60 min stale - would be closed with 5-min threshold, but not with 10-min
        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-2),
            LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-60), // 60 min ago < 90 min threshold
            EndedAtUtc = null,
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Trip",
            RegionNameSnapshot = "Region",
            PlaceNameSnapshot = "Place"
        };

        db.PlaceVisitEvents.Add(visit);
        await db.SaveChangesAsync();

        var settingsService = new ApplicationSettingsService(db, cache);
        var job = new VisitCleanupJob(db, settingsService, NullLogger<VisitCleanupJob>.Instance);

        var jobDetail = JobBuilder.Create<VisitCleanupJob>().WithIdentity("visitCleanup", "tests").Build();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(jobDetail);

        // Act
        await job.Execute(context.Object);

        // Assert - visit should NOT be closed because threshold is 90 min
        var result = await db.PlaceVisitEvents.FindAsync(visit.Id);
        Assert.Null(result!.EndedAtUtc); // Should still be open
    }

    #endregion
}
