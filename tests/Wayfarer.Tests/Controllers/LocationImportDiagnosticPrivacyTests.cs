using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Quartz;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Locks Location import storage and diagnostic output to bounded, private values.</summary>
public sealed class LocationImportDiagnosticPrivacyTests : TestBase
{
    private const string UserSentinel = "private-user-507";
    private const string FileSentinel = "private-history-507.csv";
    private const string ExceptionSentinel =
        "private-error C:\\private-507 https://provider.invalid?key=credential-507 payload=provider-secret-507";

    [Fact]
    public async Task UploadUsesOpaqueBasenameAndLogsOnlyPersistedImportIdentity()
    {
        using var root = new TemporaryDirectory("private-directory-507");
        var db = CreateDbContext();
        var logs = new TestLogProvider();
        var controller = BuildController(db, root.Path, logs, UserSentinel);
        await using var content = new MemoryStream("Latitude,Longitude"u8.ToArray());
        var file = new FormFile(content, 0, content.Length, "file", FileSentinel);

        await controller.Upload(new LocationImportUploadViewModel
        {
            File = file,
            FileType = LocationImportFileType.Csv
        });

        var import = Assert.Single(db.LocationImports);
        var basename = Path.GetFileName(import.FilePath);
        Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(basename), "N", out _));
        Assert.Equal(".csv", Path.GetExtension(basename));
        Assert.All(logs.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("Location import upload staged") &&
            entry.Fields.TryGetValue("ImportId", out var id) && Equals(id, import.Id));
    }

    [Fact]
    public async Task UploadFailureUsesBoundedLogAuditAndRemovesOnlyNewStagedFile()
    {
        using var root = new TemporaryDirectory("private-directory-507");
        var db = CreateDbContext();
        var logs = new TestLogProvider();
        var controller = BuildController(db, root.Path, logs, UserSentinel);
        var file = new Mock<IFormFile>();
        file.SetupGet(item => item.FileName).Returns(FileSentinel);
        file.SetupGet(item => item.Length).Returns(10);
        file.Setup(item => item.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(ExceptionSentinel));

        await controller.Upload(new LocationImportUploadViewModel
        {
            File = file.Object,
            FileType = LocationImportFileType.Csv
        });

        Assert.All(logs.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.All(db.AuditLogs, audit => AssertPrivateTextAbsent(audit.Details, root.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, "Uploads", "Temp")));
        Assert.Equal("An unexpected error occurred. Please try again later.", controller.TempData["AlertMessage"]);
    }

    [Fact]
    public async Task WorkerMissingFileAndSchedulingFailureEmitOnlyBoundedDiagnostics()
    {
        using var root = new TemporaryDirectory("private-directory-507");
        var missingPath = Path.Combine(root.Path, FileSentinel);
        var db = CreateDbContext();
        db.LocationImports.Add(new LocationImport
        {
            Id = 507,
            UserId = UserSentinel,
            FileType = LocationImportFileType.Csv,
            FilePath = missingPath,
            Status = ImportStatus.InProgress,
            TotalRecords = 0,
            LastProcessedIndex = 0
        });
        await db.SaveChangesAsync();
        var logs = new TestLogProvider();
        var service = BuildService(db, logs, null);

        await service.ProcessImport(507, CancellationToken.None);

        var import = Assert.Single(db.LocationImports);
        Assert.Equal(ImportStatus.Failed, import.Status);
        Assert.Equal("Import staged file unavailable.", import.ErrorMessage);
        Assert.All(logs.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("location-import-file-unavailable"));
    }

    [Fact]
    public async Task WorkerProcessingAndSchedulingFailuresNeverCaptureExceptions()
    {
        using var root = new TemporaryDirectory("private-directory-507");
        var filePath = Path.Combine(root.Path, FileSentinel);
        await File.WriteAllTextAsync(filePath,
            "Latitude,Longitude,TimestampUtc\r\n37.1,-122.2,2025-01-01T00:00:00Z");
        var db = CreateDbContext();
        db.LocationImports.Add(new LocationImport
        {
            Id = 508,
            UserId = UserSentinel,
            FileType = LocationImportFileType.Csv,
            FilePath = filePath,
            Status = ImportStatus.InProgress,
            EnrichmentRequested = true,
            TotalRecords = 0,
            LastProcessedIndex = 0
        });
        await db.SaveChangesAsync();
        var handoff = new Mock<IImportEnrichmentHandoff>();
        handoff.Setup(item => item.EnsureAsync(UserSentinel, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(ExceptionSentinel));
        var logs = new TestLogProvider();
        var service = BuildService(db, logs, handoff.Object);

        await service.ProcessImport(508, CancellationToken.None);

        Assert.All(logs.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.DoesNotContain(logs.Entries, entry => entry.Exception is not null);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("location-import-enrichment-reconciliation-required"));
    }

    [Fact]
    public async Task WorkerProcessingFailurePersistsAndLogsOnlyBoundedCategory()
    {
        using var root = new TemporaryDirectory("private-directory-507");
        var filePath = Path.Combine(root.Path, FileSentinel);
        await File.WriteAllTextAsync(filePath, "private parser payload");
        await using var exclusive = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var db = CreateDbContext();
        db.LocationImports.Add(new LocationImport
        {
            Id = 509,
            UserId = UserSentinel,
            FileType = LocationImportFileType.Csv,
            FilePath = filePath,
            Status = ImportStatus.InProgress,
            TotalRecords = 0,
            LastProcessedIndex = 0
        });
        await db.SaveChangesAsync();
        var logs = new TestLogProvider();

        await BuildService(db, logs, null).ProcessImport(509, CancellationToken.None);

        var import = Assert.Single(db.LocationImports);
        Assert.Equal(ImportStatus.Failed, import.Status);
        Assert.Equal("Import processing failed.", import.ErrorMessage);
        Assert.All(logs.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("location-import-processing-failed"));
    }

    private static void AssertPrivateTextAbsent(TestLogProvider.TestLogEntry entry, string privateDirectory)
    {
        Assert.Null(entry.Exception);
        AssertPrivateTextAbsent(entry.Message, privateDirectory);
        Assert.All(entry.Fields.Values, value => AssertPrivateTextAbsent(value?.ToString() ?? string.Empty, privateDirectory));
    }

    private static void AssertPrivateTextAbsent(string value, string privateDirectory)
    {
        Assert.DoesNotContain(UserSentinel, value, StringComparison.Ordinal);
        Assert.DoesNotContain(FileSentinel, value, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDirectory, value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExceptionSentinel, value, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-507", value, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-507", value, StringComparison.Ordinal);
    }

    private LocationImportController BuildController(ApplicationDbContext db, string contentRoot,
        TestLogProvider logs, string userId)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.ContentRootPath).Returns(contentRoot);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var controller = new LocationImportController(db, loggerFactory.CreateLogger<LocationImportController>(),
            environment.Object, Mock.Of<IScheduler>(),
            Mock.Of<ILocationEnrichmentPresentationProjector>(), importLifecycle: Mock.Of<ILocationImportLifecycle>());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private LocationImportService BuildService(ApplicationDbContext db, TestLogProvider logs,
        IImportEnrichmentHandoff? handoff)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var reverse = new ReverseGeocodingService(new HttpClient(),
            loggerFactory.CreateLogger<BaseApiController>());
        return new LocationImportService(db, reverse, loggerFactory.CreateLogger<LocationImportService>(),
            new LocationDataParserFactory(loggerFactory), new SseService(), handoff);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string suffix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}-{suffix}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
