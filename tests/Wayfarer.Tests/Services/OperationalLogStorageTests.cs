using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Serilog;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Jobs;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies the actual file sink, Admin reader and cleanup against one external log authority.</summary>
public sealed class OperationalLogStorageTests : TestBase
{
    /// <summary>Startup registers the resolved instance and the daily file is visible through Admin.</summary>
    [Fact]
    public async Task StartupFileSinkAndAdminShareExternalLogRoot()
    {
        var root = CreateTestDirectory();
        var appRoot = Path.Combine(root, "published");
        Directory.CreateDirectory(appRoot);
        await File.WriteAllTextAsync(Path.Combine(appRoot, "appsettings.json"), "{}");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = appRoot, EnvironmentName = "Production"
        });
        var expected = TestDirectory.Storage(root);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataRoot"] = expected.DataRoot, ["Storage:CacheRoot"] = expected.CacheRoot,
            ["Storage:LogRoot"] = expected.LogRoot, ["Storage:TempRoot"] = expected.TempRoot,
            // The legacy key cannot redirect logs; PostgreSQL availability is not this file-sink test's contract.
            ["Logging:LogFilePath:Default"] = Path.Combine(appRoot, "Logs", "ignored-.log"),
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"
        });
        var storage = ApplicationConfiguration.Configure(builder);
        var previousLogger = Log.Logger;
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(appRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            ApplicationConfiguration.ConfigureLogging(builder, storage);
            await using var app = builder.Build();
            Assert.Same(storage, app.Services.GetRequiredService<StoragePaths>());
            Log.Information("External log authority qualification");
            var name = $"wayfarer-{DateTime.Now:yyyyMMdd}.log";
            var controller = new LogsController(NullLogger<LogsController>.Instance, CreateDbContext(), storage);
            var list = Assert.IsType<ViewResult>(controller.Index());
            Assert.Contains(Assert.IsType<List<LogFileInfo>>(list.Model), file => file.FileName == name && file.IsCurrent);
            var download = Assert.IsType<FileStreamResult>(controller.DownloadLog(name));
            using var reader = new StreamReader(download.FileStream);
            Assert.Contains("External log authority qualification", await reader.ReadToEndAsync());
            Assert.False(Directory.Exists(Path.Combine(appRoot, "Logs")));
            Assert.False(Directory.Exists(Path.Combine(appRoot, "wwwroot")));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(appRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>An unusable configured root aborts logging setup rather than falling back into the application.</summary>
    [Fact]
    public void UnusableLogRootFailsClearly()
    {
        var root = CreateTestDirectory();
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        Assert.Throws<IOException>(() => ApplicationConfiguration.ConfigureLogging(builder, TestDirectory.Storage(root, blocked)));
    }

    /// <summary>Missing roots complete with zero deletions; arbitrary filenames and nested logs survive cleanup.</summary>
    [Fact]
    public async Task CleanupIsBoundedToDailyFilesAndMissingRootIsSafe()
    {
        var storage = TestDirectory.Storage(CreateTestDirectory());
        var detail = JobBuilder.Create<LogCleanupJob>().Build();
        var context = Mock.Of<IJobExecutionContext>(c => c.JobDetail == detail);
        var job = new LogCleanupJob(storage, NullLogger<LogCleanupJob>.Instance);
        await job.Execute(context);
        Assert.Equal("Completed", detail.JobDataMap["Status"]);
        Assert.Equal("Deleted 0 old log files", detail.JobDataMap["StatusMessage"]);
        Directory.CreateDirectory(Path.Combine(storage.LogRoot, "nested"));
        var unknown = Path.Combine(storage.LogRoot, "wayfarer-evidence.log");
        var nested = Path.Combine(storage.LogRoot, "nested", "wayfarer-20200101.log");
        foreach (var path in new[] { unknown, nested })
        {
            File.WriteAllText(path, "keep");
            File.SetCreationTime(path, DateTime.Now.AddYears(-1));
        }
        await job.Execute(context);
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(nested));
        var controller = new LogsController(NullLogger<LogsController>.Instance, CreateDbContext(), storage);
        Assert.Empty(Assert.IsType<List<LogFileInfo>>(Assert.IsType<ViewResult>(controller.Index()).Model));
    }
}
