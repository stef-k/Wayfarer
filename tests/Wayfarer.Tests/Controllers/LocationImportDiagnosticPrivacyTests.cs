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
    private const string DiagnosticSinkSentinel = "private-diagnostic-sink-error-507";

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
    public async Task UploadPreCommitFailureCleansExactStagedFileBeforeThrowingDiagnostics()
    {
        using var root = new TemporaryDirectory("precommit-cleanup-order-507");
        var uploadDirectory = Path.Combine(root.Path, "Uploads", "Temp");
        Directory.CreateDirectory(uploadDirectory);
        var preservedFile = Path.Combine(uploadDirectory, "fixture-owned-preserved.csv");
        await File.WriteAllTextAsync(preservedFile, "preserve");
        var db = CreateDbContext();
        var logger = new ThrowingPreCommitLogger(uploadDirectory, preservedFile);
        var controller = BuildController(db, root.Path, logger, UserSentinel);
        var file = new Mock<IFormFile>();
        file.SetupGet(item => item.FileName).Returns(FileSentinel);
        file.SetupGet(item => item.Length).Returns(10);
        file.Setup(item => item.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(ExceptionSentinel));

        var result = await controller.Upload(new LocationImportUploadViewModel
        {
            File = file.Object,
            FileType = LocationImportFileType.Csv
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.LocationImports);
        Assert.Equal([preservedFile], Directory.EnumerateFiles(uploadDirectory));
        Assert.Equal(1, logger.PrimaryFailureAttempts);
        Assert.Equal(1, logger.AlertAttempts);
        Assert.Equal(("An unexpected error occurred. Please try again later.", "danger"),
            (controller.TempData["AlertMessage"], controller.TempData["AlertType"]));
        Assert.All(logger.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
    }

    [Fact]
    public async Task UploadCleanupAndDiagnosticFailuresPreservePrimaryBoundedRedirect()
    {
        using var root = new TemporaryDirectory("precommit-cleanup-failure-507");
        var db = CreateDbContext();
        var logger = new ThrowingFailureSinksLogger();
        var controller = BuildController(db, root.Path, logger, UserSentinel);
        string? stagedFile = null;
        var file = new Mock<IFormFile>();
        file.SetupGet(item => item.FileName).Returns(FileSentinel);
        file.SetupGet(item => item.Length).Returns(10);
        file.Setup(item => item.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((stream, _) =>
            {
                stagedFile = Assert.IsType<FileStream>(stream).Name;
                stream.Dispose();
                File.SetAttributes(stagedFile, FileAttributes.ReadOnly);
            })
            .ThrowsAsync(new InvalidOperationException(ExceptionSentinel));

        try
        {
            var result = await controller.Upload(new LocationImportUploadViewModel
            {
                File = file.Object,
                FileType = LocationImportFileType.Csv
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Empty(db.LocationImports);
            Assert.NotNull(stagedFile);
            Assert.True(File.Exists(stagedFile));
            Assert.Equal(1, logger.PrimaryFailureAttempts);
            Assert.Equal(1, logger.CleanupFailureAttempts);
            Assert.Equal(1, logger.AlertAttempts);
            Assert.All(logger.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        }
        finally
        {
            if (stagedFile is not null && File.Exists(stagedFile))
            {
                File.SetAttributes(stagedFile, FileAttributes.Normal);
                File.Delete(stagedFile);
            }
        }
    }

    [Fact]
    public async Task UploadSuccessLoggerFailurePreservesCommittedImportAndStagedFile()
    {
        using var root = new TemporaryDirectory("committed-upload-507");
        var db = CreateDbContext();
        var logger = new ThrowingUploadSuccessLogger();
        var controller = BuildController(db, root.Path, logger, UserSentinel);
        await using var content = new MemoryStream("Latitude,Longitude"u8.ToArray());
        var file = new FormFile(content, 0, content.Length, "file", FileSentinel);

        var result = await controller.Upload(new LocationImportUploadViewModel
        {
            File = file,
            FileType = LocationImportFileType.Csv
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var import = Assert.Single(db.LocationImports);
        var stagedFile = Assert.Single(Directory.EnumerateFiles(Path.Combine(root.Path, "Uploads", "Temp")));
        Assert.Equal(stagedFile, import.FilePath);
        Assert.True(File.Exists(import.FilePath));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("upload failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("cleanup failed", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => AssertPrivateTextAbsent(entry, root.Path));
        Assert.NotEqual("An unexpected error occurred. Please try again later.", controller.TempData["AlertMessage"]);
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
        Assert.DoesNotContain(DiagnosticSinkSentinel, value, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-507", value, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-507", value, StringComparison.Ordinal);
    }

    private LocationImportController BuildController(ApplicationDbContext db, string contentRoot,
        TestLogProvider logs, string userId)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.ContentRootPath).Returns(contentRoot);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        return BuildController(db, contentRoot, loggerFactory.CreateLogger<LocationImportController>(), userId);
    }

    private LocationImportController BuildController(ApplicationDbContext db, string contentRoot,
        ILogger<LocationImportController> logger, string userId)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.ContentRootPath).Returns(contentRoot);
        var controller = new LocationImportController(db, logger,
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

    /// <summary>Fails only the success diagnostic while retaining bounded later entries for assertions.</summary>
    private sealed class ThrowingUploadSuccessLogger : ILogger<LocationImportController>
    {
        private readonly List<TestLogProvider.TestLogEntry> entries = [];

        internal IReadOnlyList<TestLogProvider.TestLogEntry> Entries => entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("Location import upload staged", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ExceptionSentinel);
            }

            var fields = state is IEnumerable<KeyValuePair<string, object?>> structuredState
                ? structuredState.Where(field => field.Key != "{OriginalFormat}")
                    .ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(logLevel, nameof(LocationImportController), eventId, fields, message, exception));
        }
    }

    /// <summary>Throws from failure diagnostics after verifying request-local cleanup already completed.</summary>
    private sealed class ThrowingPreCommitLogger(string uploadDirectory, string preservedFile)
        : ILogger<LocationImportController>
    {
        private readonly List<TestLogProvider.TestLogEntry> entries = [];

        internal IReadOnlyList<TestLogProvider.TestLogEntry> Entries => entries;
        internal int PrimaryFailureAttempts { get; private set; }
        internal int AlertAttempts { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var fields = state is IEnumerable<KeyValuePair<string, object?>> structuredState
                ? structuredState.Where(field => field.Key != "{OriginalFormat}")
                    .ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(logLevel, nameof(LocationImportController), eventId, fields, message, exception));

            if (message.Contains("Location import upload failed", StringComparison.Ordinal))
            {
                PrimaryFailureAttempts++;
                Assert.Equal([preservedFile], Directory.EnumerateFiles(uploadDirectory));
                throw new InvalidOperationException(DiagnosticSinkSentinel);
            }

            if (message.Contains("Alert:", StringComparison.Ordinal))
            {
                AlertAttempts++;
                throw new InvalidOperationException(DiagnosticSinkSentinel);
            }
        }
    }

    /// <summary>Throws independently from every pre-commit diagnostic and presentation log phase.</summary>
    private sealed class ThrowingFailureSinksLogger : ILogger<LocationImportController>
    {
        private readonly List<TestLogProvider.TestLogEntry> entries = [];

        internal IReadOnlyList<TestLogProvider.TestLogEntry> Entries => entries;
        internal int PrimaryFailureAttempts { get; private set; }
        internal int CleanupFailureAttempts { get; private set; }
        internal int AlertAttempts { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var fields = state is IEnumerable<KeyValuePair<string, object?>> structuredState
                ? structuredState.Where(field => field.Key != "{OriginalFormat}")
                    .ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(logLevel, nameof(LocationImportController), eventId, fields, message, exception));

            if (message.Contains("Location import upload cleanup failed", StringComparison.Ordinal))
            {
                CleanupFailureAttempts++;
            }
            else if (message.Contains("Location import upload failed", StringComparison.Ordinal))
            {
                PrimaryFailureAttempts++;
            }
            else if (message.Contains("Alert:", StringComparison.Ordinal))
            {
                AlertAttempts++;
            }
            else
            {
                return;
            }

            throw new InvalidOperationException(DiagnosticSinkSentinel);
        }
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
