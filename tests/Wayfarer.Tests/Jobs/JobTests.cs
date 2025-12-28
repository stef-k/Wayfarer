using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Parsers;
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

        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        var job = new AuditLogCleanupJob(db);
        await job.Execute(context.Object);

        var remaining = Assert.Single(db.AuditLogs);
        Assert.Equal("recent", remaining.UserId);
    }

    [Fact]
    public async Task AuditLogCleanupJob_RespectsCanellationToken()
    {
        var db = CreateDbContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(cts.Token);

        var job = new AuditLogCleanupJob(db);

        await Assert.ThrowsAsync<OperationCanceledException>(() => job.Execute(context.Object));
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
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(db);
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
}
