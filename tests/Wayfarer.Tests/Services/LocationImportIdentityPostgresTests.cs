using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Point = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Tests.Services;

/// <summary>Proves identity failures precede writes and preserve durable progress through real PostgreSQL workers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportIdentityPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>A bad identity beyond the first batch fails the full file before replacing totals or writing Locations.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv, false)]
    [InlineData(LocationImportFileType.Csv, true)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, false)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, true)]
    public async Task LateInvalidIdentity_FailsFreshImportBeforeAnyWrites(LocationImportFileType format, bool multipleZeros)
    {
        var user = await fixture.CreateUserAsync();
        var keys = Enumerable.Range(0, 52).Select(_ => Guid.NewGuid()).ToArray();
        if (multipleZeros) keys[0] = Guid.Empty;
        var path = ImportStaging.TempFile();
        var logs = new TestLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var client = new HttpClient();
        try
        {
            await WriteInputAsync(path, format, keys, multipleZeros ? Guid.Empty.ToString("D") : "private-malformed-uuid");
            await using var db = fixture.CreateContext();
            var import = Import(user.Id, path, format);
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            var worker = Worker(client, loggerFactory);
            await worker.ProcessImport(import.Id, import.ExecutionEpoch, CancellationToken.None);
            await db.Entry(import).ReloadAsync();
            var rows = await db.Locations.CountAsync(row => row.UserId == user.Id);
            output.WriteLine($"Fresh {format}, multipleZeros={multipleZeros}: status={import.Status}, total={import.TotalRecords}, checkpoint={import.LastProcessedIndex}, duplicates={import.SkippedDuplicates}, rows={rows}");
            Assert.Equal(ImportStatus.Failed, import.Status);
            Assert.Equal((0, 0, 0, 0), (import.TotalRecords, import.LastProcessedIndex, import.SkippedDuplicates, rows));
            Assert.False(import.ProjectionPending);
            AssertDiagnostic(import, multipleZeros, path, logs, 1);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Repeated Start preserves historical checkpoints and owner edits; a corrected new upload recovers remaining keys.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv, false)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, true)]
    public async Task InvalidHistoricalFile_PreservesCheckpointAcrossStart_AndCorrectedNewImportRecovers(
        LocationImportFileType format, bool multipleZeros)
    {
        var user = await fixture.CreateUserAsync();
        var keys = Enumerable.Range(0, 52).Select(_ => Guid.NewGuid()).ToArray();
        if (multipleZeros) keys[0] = Guid.Empty;
        var path = ImportStaging.TempFile();
        var correctedPath = ImportStaging.TempFile();
        var logs = new TestLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var client = new HttpClient();
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"identity-{Guid.NewGuid():N}"
        }).GetScheduler();
        try
        {
            await WriteInputAsync(path, format, keys, multipleZeros ? Guid.Empty.ToString("D") : "private-malformed-uuid");
            await WriteInputAsync(correctedPath, format, keys, keys[^1].ToString("D"));
            await using var db = fixture.CreateContext();
            var previous = keys.Take(50).Select((key, index) => new Location
            {
                UserId = user.Id, IdempotencyKey = key,
                Timestamp = DateTime.UnixEpoch.AddSeconds(index), LocalTimestamp = DateTime.UnixEpoch.AddSeconds(index),
                Coordinates = new Point(23.2, 41.1) { SRID = 4326 }, TimeZoneId = "UTC", Notes = "Owner edit"
            }).ToArray();
            db.Locations.AddRange(previous);
            var import = Import(user.Id, path, format);
            import.Status = ImportStatus.Stopped;
            import.TotalRecords = 52;
            import.LastProcessedIndex = 50;
            import.SkippedDuplicates = 3;
            import.UnclassifiedActivityCount = 2;
            import.RemainingEnrichmentCount = 4;
            import.LastImportedRecord = "kept-checkpoint";
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            var worker = Worker(client, loggerFactory);
            var lifecycle = new LocationImportLifecycle(ImportStaging.Files, new FixtureFactory(fixture), scheduler,
                NullLogger<LocationImportLifecycle>.Instance);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.True((await lifecycle.StartAsync(user.Id, import.Id)).Succeeded);
                await db.Entry(import).ReloadAsync();
                var execution = new Mock<IJobExecutionContext>();
                execution.SetupGet(item => item.JobDetail).Returns(
                    (await scheduler.GetJobDetail(LocationImportSchedulerKeys.Job(import.Id, import.ExecutionEpoch)))!);
                execution.SetupProperty(item => item.Result);
                await new LocationImportJob(worker, NullLogger<LocationImportJob>.Instance, lifecycle).Execute(execution.Object);
                await db.Entry(import).ReloadAsync();
                output.WriteLine($"Historical {format}, attempt={attempt}: outcome={execution.Object.Result}, status={import.Status}, total={import.TotalRecords}, checkpoint={import.LastProcessedIndex}");
                Assert.Equal(LocationImportExecutionOutcome.Failed, execution.Object.Result);
                Assert.Equal(ImportStatus.Failed, import.Status);
                Assert.Equal((52, 50, 3, 2, 4, "kept-checkpoint"), (import.TotalRecords, import.LastProcessedIndex,
                    import.SkippedDuplicates, import.UnclassifiedActivityCount, import.RemainingEnrichmentCount, import.LastImportedRecord));
                Assert.False(import.ProjectionPending);
                AssertDiagnostic(import, multipleZeros, path, logs, attempt + 1);
                Assert.Equal(50, await db.Locations.CountAsync(row => row.UserId == user.Id));
                Assert.All(await db.Locations.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync(), row =>
                    Assert.Equal(("Owner edit", 23.2, 41.1), (row.Notes, row.Coordinates.X, row.Coordinates.Y)));
            }
            var recovery = Import(user.Id, correctedPath, format);
            db.LocationImports.Add(recovery);
            await db.SaveChangesAsync();
            await worker.ProcessImport(recovery.Id, recovery.ExecutionEpoch, CancellationToken.None);
            await db.Entry(recovery).ReloadAsync();
            Assert.Equal(ImportStatus.Completed, recovery.Status);
            Assert.Equal((52, 52, 50), (recovery.TotalRecords, recovery.LastProcessedIndex, recovery.SkippedDuplicates));
            Assert.Null(recovery.ErrorMessage);
            var recovered = await db.Locations.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync();
            Assert.Equal(52, recovered.Count);
            Assert.Equal(previous.Select(row => (row.Id, row.IdempotencyKey, row.Notes, row.Coordinates.X, row.Coordinates.Y)),
                recovered.OrderBy(row => row.Id).Take(50).Select(row => (row.Id, row.IdempotencyKey, row.Notes, row.Coordinates.X, row.Coordinates.Y)));
            Assert.All(keys.Skip(50), key => Assert.Single(recovered, row => row.IdempotencyKey == key));
            output.WriteLine("Corrected new import: total/checkpoint=52/52, duplicates=50, Locations=52; all prior IDs and edits preserved.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(correctedPath);
            await scheduler.Shutdown();
        }
    }

    /// <summary>Checks only fixed diagnostic content and safe structured fields after terminal projection.</summary>
    private static void AssertDiagnostic(LocationImport import, bool multipleZeros, string path, TestLogProvider logs, int count)
    {
        var message = multipleZeros
            ? "Multiple zero IdempotencyKeys. Give distinct captures unique nonzero UUIDs and upload the corrected file as a new import."
            : "Invalid IdempotencyKey. Correct UUID values and upload the corrected file as a new import.";
        Assert.Equal(message, import.ErrorMessage);
        Assert.InRange(import.ErrorMessage!.Length, 1, 200);
        var failures = logs.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();
        Assert.Equal(count, failures.Count);
        Assert.All(failures, entry =>
        {
            Assert.Equal(multipleZeros ? "location-import-multiple-zero-idempotency-keys" : "location-import-invalid-idempotency-key", entry.Fields["Code"]);
            Assert.Equal(2, entry.Fields.Count);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(path, entry.Message);
            Assert.DoesNotContain("private-", entry.Message);
            Assert.DoesNotContain("00000000-", entry.Message);
        });
    }

    /// <summary>Uses existing production parser, staged-file and batch ownership seams without provider calls.</summary>
    private LocationImportService Worker(HttpClient client, ILoggerFactory logs) => new(ImportStaging.Files,
        new FixtureFactory(fixture), new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
        logs.CreateLogger<LocationImportService>(), new LocationDataParserFactory(logs), new SseService());

    /// <summary>Creates explicit current-generation import authority with no preexisting progress.</summary>
    private static LocationImport Import(string userId, string path, LocationImportFileType format) => new()
    {
        UserId = userId, FilePath = path, FileType = format, Status = ImportStatus.InProgress,
        ExecutionEpoch = 7, TotalRecords = 0, LastProcessedIndex = 0
    };

    /// <summary>Places the invalid key at row 52, after an ordinary batch boundary and a valid remaining capture.</summary>
    private static Task WriteInputAsync(string path, LocationImportFileType format, Guid[] keys, string finalKey)
    {
        var payload = format == LocationImportFileType.Csv
            ? "Latitude,Longitude,TimestampUtc,IdempotencyKey,Notes\r\n" + string.Join("\r\n", keys.Select((key, index) =>
                $"40.1,22.2,{DateTime.UnixEpoch.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture)},{(index == keys.Length - 1 ? finalKey : key.ToString("D"))},private-notes"))
            : JsonSerializer.Serialize(new
            {
                type = "FeatureCollection",
                features = keys.Select((key, index) => new
                {
                    type = "Feature", geometry = new { type = "Point", coordinates = new[] { 22.2, 40.1 } },
                    properties = new
                    {
                        TimestampUtc = DateTime.UnixEpoch.AddSeconds(index),
                        IdempotencyKey = index == keys.Length - 1 ? finalKey : key.ToString("D"), Notes = "private-notes"
                    }
                })
            });
        return File.WriteAllTextAsync(path, payload);
    }

    /// <summary>Retains real independent PostgreSQL contexts for each worker operation.</summary>
    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        /// <inheritdoc />
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
