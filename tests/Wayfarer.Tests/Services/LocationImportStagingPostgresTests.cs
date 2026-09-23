using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the new file-reference boundary against real relational upload and recovery authority.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportStagingPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_PersistsLogicalReferenceOrRemovesPhysicalFileOnDatabaseFailure(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var files = ImportStaging.Create(root);
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(new FailImportSave(fail));
        var controller = new LocationImportController(files, db, NullLogger<LocationImportController>.Instance,
            Mock.Of<IWebHostEnvironment>(), Scheduler(), Mock.Of<ILocationEnrichmentPresentationProjector>(),
            importLifecycle: Mock.Of<ILocationImportLifecycle>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"))
        } };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        await using var stream = new MemoryStream("Latitude,Longitude"u8.ToArray());
        try
        {
            await controller.Upload(new LocationImportUploadViewModel
            {
                FileType = LocationImportFileType.Csv,
                File = new FormFile(stream, 0, stream.Length, "file", "private-original.CSV")
            });
            await using var verification = fixture.CreateContext();
            var rows = await verification.LocationImports.Where(x => x.UserId == user.Id).ToListAsync();
            if (fail)
            {
                Assert.Empty(rows);
                Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath));
            }
            else
            {
                var row = Assert.Single(rows);
                Assert.Matches(@"^imports/[0-9a-f]{32}\.csv$", row.FilePath);
                Assert.True(files.TryResolve(row.FilePath, out var physical));
                Assert.Equal(Path.Combine(root, "data", "uploads", "imports", row.FilePath[8..]), physical);
                Assert.True(File.Exists(physical));
            }
            Assert.False(Directory.Exists(Path.Combine(root, "app", "Uploads")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgresTheory]
    [InlineData("canonical")]
    [InlineData("legacy")]
    [InlineData("outside")]
    [InlineData("foreign")]
    public async Task Worker_ResolvesOnlyOwnedReferencesWithoutRewriting(string kind)
    {
        var reference = LocationImportStagedFiles.CreateReference(LocationImportFileType.Csv);
        Assert.True(ImportStaging.Files.TryResolve(reference, out var path));
        Directory.CreateDirectory(ImportStaging.Files.DirectoryPath);
        if (kind == "legacy") reference = path = ImportStaging.TempFile();
        if (kind == "outside") reference = path = Path.GetTempFileName();
        if (kind == "foreign") reference = @"Z:\private\foreign.csv";
        await File.WriteAllTextAsync(path, "Latitude,Longitude,TimestampUtc\r\n37.1,22.2,2026-09-01T00:00:00Z");
        var row = await SeedAsync(reference, ImportStatus.InProgress);
        try
        {
            var worker = new LocationImportService(ImportStaging.Files, new Contexts(fixture),
                new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
            var outcome = await worker.ProcessImportExecution(row.Id, 0, default);
            var allowed = kind is "canonical" or "legacy";
            Assert.Equal(allowed ? LocationImportExecutionOutcome.Completed :
                LocationImportExecutionOutcome.StagedFileUnavailable, outcome);
            await using var db = fixture.CreateContext();
            Assert.Equal(allowed ? 1 : 0, await db.Locations.CountAsync(x => x.UserId == row.UserId));
            Assert.Equal(reference, (await db.LocationImports.FindAsync(row.Id))!.FilePath);
        }
        finally { File.Delete(path); }
    }

    [PostgresTheory]
    [InlineData("canonical")]
    [InlineData("legacy")]
    [InlineData("missing")]
    [InlineData("missing-directory")]
    [InlineData("outside")]
    [InlineData("foreign")]
    [InlineData("malformed")]
    public async Task Delete_RetainsUnsafeAuthorityUntilExplicitRepairThenConverges(string kind)
    {
        var files = kind == "missing-directory"
            ? ImportStaging.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) : ImportStaging.Files;
        var reference = LocationImportStagedFiles.CreateReference(LocationImportFileType.Csv);
        Assert.True(files.TryResolve(reference, out var path));
        if (kind != "missing-directory") Directory.CreateDirectory(files.DirectoryPath);
        if (kind == "legacy") reference = path = ImportStaging.TempFile();
        if (kind == "outside") reference = path = Path.GetTempFileName();
        if (kind == "foreign") reference = @"Z:\private\foreign.csv";
        if (kind == "malformed") reference = "imports/../outside.csv";
        if (kind is not "missing" and not "missing-directory") await File.WriteAllTextAsync(path, "preserve until cleanup authority is valid");
        var row = await SeedAsync(reference, ImportStatus.Completed);
        var observer = new DeletionObserver(fixture);
        var scheduler = Scheduler();
        var lifecycle = new LocationImportLifecycle(files, new Contexts(fixture), scheduler,
            NullLogger<LocationImportLifecycle>.Instance, observer);
        var reconciler = new LocationImportReconciler(files, new Contexts(fixture), scheduler,
            NullLogger<LocationImportReconciler>.Instance);
        try
        {
            var result = await lifecycle.DeleteAsync(row.UserId, row.Id);
            var unsafeReference = kind is "outside" or "foreign" or "malformed";
            Assert.Equal(unsafeReference ? LocationImportCommandCode.ProjectionPending :
                LocationImportCommandCode.Accepted, result.Code);
            await reconciler.ReconcileAsync();
            await using (var db = fixture.CreateContext())
            {
                var pending = await db.LocationImports.FindAsync(row.Id);
                if (unsafeReference)
                {
                    Assert.NotNull(pending!.DeletionRequestedAtUtc);
                    Assert.Equal(reference, pending.FilePath);
                    Assert.True(File.Exists(path));
                    Assert.Null(observer.Path);
                    // Simulate a future explicit migration/repair; ordinary operations never perform this write.
                    pending.FilePath = LocationImportStagedFiles.CreateReference(LocationImportFileType.Csv);
                    await db.SaveChangesAsync();
                }
                else
                {
                    Assert.Null(pending);
                    Assert.Equal(path, observer.Path);
                    Assert.False(File.Exists(path));
                }
            }
            await reconciler.ReconcileAsync();
            await reconciler.ReconcileAsync();
            await using var final = fixture.CreateContext();
            Assert.Null(await final.LocationImports.FindAsync(row.Id));
            if (unsafeReference) Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Seeds durable row authority without reaching into the worker's implementation.</summary>
    private async Task<LocationImport> SeedAsync(string reference, ImportStatus status)
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var row = new LocationImport
        {
            UserId = user.Id, FilePath = reference, FileType = LocationImportFileType.Csv,
            Status = status, TotalRecords = 0, LastProcessedIndex = 0
        };
        db.LocationImports.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>An absent scheduler projection allows file cleanup after the relational intent commit.</summary>
    private static IScheduler Scheduler()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default)).ReturnsAsync([]);
        scheduler.Setup(x => x.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default)).ReturnsAsync([]);
        return scheduler.Object;
    }

    /// <summary>Injects a relational save failure only for a newly staged import.</summary>
    private sealed class FailImportSave(bool enabled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (enabled && eventData.Context!.ChangeTracker.Entries<LocationImport>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("private database failure");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Verifies intent is visible to another PostgreSQL context before the resolved path is exposed.</summary>
    private sealed class DeletionObserver(PostgresImportTestFixture fixture) : ILocationImportLifecycleObserver
    {
        public string? Path { get; private set; }
        public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token) => Task.CompletedTask;
        public Task BeforeTerminalPersistenceAsync(int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token) => Task.CompletedTask;
        public async Task BeforeFileDeletionAsync(int importId, string filePath, CancellationToken token)
        {
            await using var db = fixture.CreateContext();
            Assert.NotNull((await db.LocationImports.FindAsync([importId], token))!.DeletionRequestedAtUtc);
            Path = filePath;
        }
    }

    /// <summary>Retains the existing per-operation relational context boundary.</summary>
    private sealed class Contexts(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
