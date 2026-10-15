using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using GeoPoint = NetTopologySuite.Geometries.Point;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>Proves keyed import work remains batch-shaped as persisted history grows.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportBoundedPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Rejects changed legacy ordinals through the production job and recovers only by a new import.</summary>
    [PostgresFact]
    public async Task LegacyCheckpoint_ChangedParserCountFails_AndNewImportRecoversMissingKey()
    {
        var user = await fixture.CreateUserAsync();
        var keys = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();
        var path = ImportStaging.TempFile();
        var logs = new TestLogProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var client = new HttpClient();
        var contexts = new FixtureFactory(fixture);
        var worker = new LocationImportService(ImportStaging.Files, contexts,
            new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
            logFactory.CreateLogger<LocationImportService>(),
            new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"checkpoint-{Guid.NewGuid():N}"
        }).GetScheduler();
        try
        {
            await File.WriteAllLinesAsync(path, ["Latitude,Longitude,TimestampUtc,IdempotencyKey,Notes",
                .. keys.Select((key, index) =>
                    $"{(index == 0 ? "100,200" : "40.1,22.2")},2026-08-25T00:00:{index:00}Z,{key:D},private-source-notes")]);
            await using var db = fixture.CreateContext();
            var legacyRows = keys.Take(50).Select((key, index) =>
            {
                var row = Location(user.Id, key, index);
                if (index == 0) row.Coordinates = new GeoPoint(200, 100) { SRID = 4326 };
                row.Notes = "Owner edit";
                return row;
            }).ToArray();
            db.Locations.AddRange(legacyRows);
            var legacy = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.Stopped, ExecutionEpoch = 6,
                TotalRecords = 51, LastProcessedIndex = 50
            };
            db.LocationImports.Add(legacy);
            await db.SaveChangesAsync();
            var original = await db.Locations.AsNoTracking().Where(row => row.UserId == user.Id)
                .OrderBy(row => row.Id).ToListAsync();
            var normalized = original.Single(row => row.IdempotencyKey == keys[0]);
            Assert.Equal((-160, 80), (normalized.Coordinates.X, normalized.Coordinates.Y));
            var lifecycle = new LocationImportLifecycle(ImportStaging.Files, contexts, scheduler,
                NullLogger<LocationImportLifecycle>.Instance);
            var job = new LocationImportJob(worker, NullLogger<LocationImportJob>.Instance, lifecycle);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.True((await lifecycle.StartAsync(user.Id, legacy.Id)).Succeeded);
                var execution = new Mock<IJobExecutionContext>();
                execution.SetupGet(item => item.JobDetail).Returns(
                    (await scheduler.GetJobDetail(LocationImportSchedulerKeys.Job(legacy.Id, 7 + attempt)))!);
                execution.SetupProperty(item => item.Result);
                await job.Execute(execution.Object);
                await db.Entry(legacy).ReloadAsync();
                var rows = await db.Locations.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync();
                output.WriteLine($"Legacy resume {attempt}: outcome={execution.Object.Result}, status={legacy.Status}, total={legacy.TotalRecords}, checkpoint={legacy.LastProcessedIndex}, rows={rows.Count}, final-valid-present={rows.Any(row => row.IdempotencyKey == keys[50])}");
                Assert.Equal(LocationImportExecutionOutcome.Failed, execution.Object.Result);
                Assert.Equal("Failed", LocationImportJobOutcome.ToHistoryStatus((LocationImportExecutionOutcome)execution.Object.Result!));
                Assert.Equal(ImportStatus.Failed, legacy.Status);
                Assert.Equal((51, 50, 0, 7 + attempt),
                    (legacy.TotalRecords, legacy.LastProcessedIndex, legacy.SkippedDuplicates, legacy.ExecutionEpoch));
                Assert.Contains("new import", legacy.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.InRange(legacy.ErrorMessage!.Length, 1, 200);
                Assert.False(legacy.ProjectionPending);
                Assert.Equal(original.Select(row => (row.Id, row.IdempotencyKey, row.Notes)),
                    rows.OrderBy(row => row.Id).Select(row => (row.Id, row.IdempotencyKey, row.Notes)));
            }
            var recovery = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 1,
                TotalRecords = 0, LastProcessedIndex = 0
            };
            db.LocationImports.Add(recovery);
            await db.SaveChangesAsync();
            await worker.ProcessImport(recovery.Id, 1, CancellationToken.None);
            await db.Entry(recovery).ReloadAsync();
            var recovered = await db.Locations.AsNoTracking().Where(row => row.UserId == user.Id)
                .OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(ImportStatus.Completed, recovery.Status);
            Assert.Equal((50, 50, 49), (recovery.TotalRecords, recovery.LastProcessedIndex, recovery.SkippedDuplicates));
            Assert.Null(recovery.ErrorMessage);
            Assert.Equal(51, recovered.Count);
            Assert.Equal(original.Select(row => (row.Id, row.IdempotencyKey, row.Notes, row.Coordinates.X, row.Coordinates.Y)),
                recovered.Take(50).Select(row => (row.Id, row.IdempotencyKey, row.Notes, row.Coordinates.X, row.Coordinates.Y)));
            Assert.Single(recovered, row => row.IdempotencyKey == keys[50]);
            output.WriteLine("New import completed: total/checkpoint=50/50, duplicates=49, rows=51; missing valid key recovered, existing history preserved.");
            Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Error));
            Assert.All(logs.Entries.Where(entry => entry.Level == LogLevel.Error), entry =>
            {
                Assert.Equal("location-import-checkpoint-incompatible", entry.Fields["Code"]);
                Assert.Equal(2, entry.Fields.Count);
                Assert.Null(entry.Exception);
                Assert.DoesNotContain(path, entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("private-source-notes", entry.Message, StringComparison.Ordinal);
                Assert.InRange(entry.Message.Length, 1, 200);
            });
        }
        finally
        {
            File.Delete(path);
            await scheduler.Shutdown();
        }
    }

    /// <summary>Preserves compatible nonzero resumes and rejects missing totals or impossible saved ordinals.</summary>
    [PostgresTheory]
    [InlineData(2, 1, true)]
    [InlineData(0, 1, false)]
    [InlineData(-1, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 3, false)]
    public async Task SavedCheckpoint_RequiresUsableMatchingTotal(int savedTotal, int checkpoint, bool compatible)
    {
        var user = await fixture.CreateUserAsync();
        var path = ImportStaging.TempFile();
        try
        {
            var keys = new[] { Guid.NewGuid(), Guid.NewGuid() };
            await File.WriteAllLinesAsync(path, ["Latitude,Longitude,TimestampUtc,IdempotencyKey",
                .. keys.Select((key, index) => $"40.1,22.2,2026-08-25T00:00:{index:00}Z,{key:D}")]);
            await using var db = fixture.CreateContext();
            db.Locations.Add(Location(user.Id, keys[0], 0));
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7,
                TotalRecords = savedTotal, LastProcessedIndex = checkpoint
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            using var client = new HttpClient();
            var worker = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture),
                new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
            await worker.ProcessImport(import.Id, 7, CancellationToken.None);
            await db.Entry(import).ReloadAsync();
            Assert.Equal(compatible ? ImportStatus.Completed : ImportStatus.Failed, import.Status);
            Assert.Equal(compatible ? 2 : savedTotal, import.TotalRecords);
            Assert.Equal(compatible ? 2 : checkpoint, import.LastProcessedIndex);
            Assert.Equal(compatible, await db.Locations.AnyAsync(row => row.UserId == user.Id && row.IdempotencyKey == keys[1]));
            Assert.Equal(0, import.SkippedDuplicates);
            if (compatible) Assert.Null(import.ErrorMessage);
            else Assert.Contains("new import", import.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Filters the original geography-wrapping witness before checkpoints and keyed replay.</summary>
    [PostgresFact]
    public async Task MixedCsv_InvalidCoordinatesNeverPersist_AndReplayPreservesCommittedHistory()
    {
        var user = await fixture.CreateUserAsync();
        var validKey = Guid.NewGuid();
        var invalidKey = Guid.NewGuid();
        var path = ImportStaging.TempFile();
        var logs = new TestLogProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var client = new HttpClient();
        var worker = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture),
            new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(logFactory), new SseService());
        try
        {
            await File.WriteAllTextAsync(path,
                "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,IdempotencyKey,Notes,DeviceModel,LocationType,Source\r\n" +
                $"100,200,2026-08-25T00:00:00Z,2026-08-25T02:00:00+02:00,Europe/Athens,{invalidKey:D},private-invalid-notes,private-invalid-device,Manual,fixture\r\n" +
                $"40.1,22.2,2026-08-25T00:00:01Z,2026-08-25T02:00:01+02:00,Europe/Athens,{validKey:D},Valid notes,Valid device,Manual,fixture\r\n");
            int? savedId = null;
            for (var replay = 0; replay < 2; replay++)
            {
                await using var db = fixture.CreateContext();
                var import = new LocationImport
                {
                    UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                    Status = ImportStatus.InProgress, ExecutionEpoch = 7,
                    TotalRecords = 0, LastProcessedIndex = 0
                };
                db.LocationImports.Add(import);
                await db.SaveChangesAsync();
                await worker.ProcessImport(import.Id, import.ExecutionEpoch, CancellationToken.None);

                var rows = await db.Locations.Where(row => row.UserId == user.Id).ToListAsync();
                foreach (var row in rows)
                    output.WriteLine($"Persisted synthetic point: keyed-invalid={row.IdempotencyKey == invalidKey}, lon={row.Coordinates.X}, lat={row.Coordinates.Y}");
                var saved = Assert.Single(rows);
                Assert.Equal(user.Id, saved.UserId);
                Assert.Equal(validKey, saved.IdempotencyKey);
                Assert.Equal((22.2, 40.1, 4326), (saved.Coordinates.X, saved.Coordinates.Y, saved.Coordinates.SRID));
                Assert.Equal(new DateTime(2026, 8, 25, 0, 0, 1, DateTimeKind.Utc), saved.Timestamp);
                Assert.Equal(saved.Timestamp, saved.LocalTimestamp);
                Assert.Equal("Europe/Athens", saved.TimeZoneId);
                Assert.Equal("Valid device", saved.DeviceModel);
                Assert.Equal("Manual", saved.LocationType);
                Assert.Equal("fixture", saved.Source);
                Assert.Equal(replay == 0 ? "Valid notes" : "Owner edit", saved.Notes);
                if (savedId.HasValue) Assert.Equal(savedId.Value, saved.Id);
                savedId = saved.Id;
                await db.Entry(import).ReloadAsync();
                Assert.Equal(ImportStatus.Completed, import.Status);
                Assert.Equal(1, import.TotalRecords);
                Assert.Equal(1, import.LastProcessedIndex);
                Assert.Equal(replay, import.SkippedDuplicates);
                Assert.Equal(7, import.ExecutionEpoch);
                Assert.Null(import.ErrorMessage);
                Assert.False(import.ProjectionPending);
                output.WriteLine($"Replay {replay}: total={import.TotalRecords}, checkpoint={import.LastProcessedIndex}, duplicates={import.SkippedDuplicates}");
                saved.Notes = "Owner edit";
                await db.SaveChangesAsync();
            }
            Assert.Equal(4, logs.Entries.Count);
            Assert.All(logs.Entries, entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Equal("location-import-invalid-coordinate", entry.Fields["Code"]);
                Assert.Equal(2, entry.Fields["Row"]);
                Assert.Equal(2, entry.Fields.Count);
                Assert.Null(entry.Exception);
                Assert.DoesNotContain("private-invalid", entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("100", entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("200", entry.Message, StringComparison.Ordinal);
            });
        }
        finally { File.Delete(path); }
    }

    /// <summary>Direct and compatibility execution cannot borrow a newer generation's authority.</summary>
    [PostgresTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    public async Task NoncurrentExecution_AndCompatibilityWrapper_CommitNothing(int epoch)
    {
        var user = await fixture.CreateUserAsync();
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllTextAsync(path,
                "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,2026-08-25T00:00:00Z\r\n");
            await using var db = fixture.CreateContext();
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7,
                TotalRecords = 0, LastProcessedIndex = 0, EnrichmentRequested = true
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            var handoff = new Moq.Mock<Wayfarer.Services.LocationEnrichment.IImportEnrichmentHandoff>();
            var worker = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture),
                new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService(), handoff.Object);

            Assert.Equal(LocationImportExecutionOutcome.Stale,
                await worker.ProcessImportExecution(import.Id, epoch, CancellationToken.None));
            await worker.ProcessImport(import.Id, epoch, CancellationToken.None);

            await db.Entry(import).ReloadAsync();
            Assert.Equal(0, await db.Locations.CountAsync(item => item.UserId == user.Id));
            Assert.Equal(0, import.TotalRecords);
            Assert.Equal(0, import.LastProcessedIndex);
            Assert.Equal(0, import.RemainingEnrichmentCount);
            Assert.Equal(ImportStatus.InProgress, import.Status);
            Assert.Empty(handoff.Invocations);
        }
        finally { File.Delete(path); }
    }

    [PostgresFact]
    public async Task KeyedExternalWriterAfterPrecheck_RetainsWinnerAndPersistsRestOfBatch()
    {
        var user = await fixture.CreateUserAsync();
        var conflictingKey = Guid.NewGuid();
        var otherKey = Guid.NewGuid();
        var path = Path.Combine(ImportStaging.LegacyDirectory, $"keyed-conflict-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path,
                "Latitude,Longitude,TimestampUtc,IdempotencyKey\r\n" +
                $"40.1,22.2,2026-08-25T00:00:00Z,{conflictingKey:D}\r\n" +
                $"40.2,22.3,2026-08-25T00:00:01Z,{otherKey:D}\r\n");
            int importId;
            await using (var seed = fixture.CreateContext())
            {
                var import = new LocationImport
                {
                    UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                    Status = ImportStatus.InProgress, ExecutionEpoch = 0,
                    TotalRecords = 0, LastProcessedIndex = 0
                };
                seed.LocationImports.Add(import);
                await seed.SaveChangesAsync();
                importId = import.Id;
            }

            var externalCommitted = false;
            var interceptor = new KeyPrecheckInterceptor(async () =>
            {
                await using var external = fixture.CreateContext();
                var winner = Location(user.Id, conflictingKey, 10_000);
                winner.Source = "external-winner";
                external.Locations.Add(winner);
                await external.SaveChangesAsync();
                externalCommitted = true;
            });
            var service = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture, interceptor, disableAutoSavepoints: true),
                new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());

            await service.ProcessImport(importId, 0, CancellationToken.None);

            Assert.True(externalCommitted);
            await using var verification = fixture.CreateContext();
            var locations = await verification.Locations.Where(item => item.UserId == user.Id).ToListAsync();
            Assert.Equal(2, locations.Count);
            Assert.Single(locations, item => item.IdempotencyKey == conflictingKey &&
                item.Source == "external-winner");
            Assert.Single(locations, item => item.IdempotencyKey == otherKey);
            var completed = await verification.LocationImports.SingleAsync(item => item.Id == importId);
            Assert.Equal(ImportStatus.Completed, completed.Status);
            Assert.Equal(2, completed.TotalRecords);
            Assert.Equal(2, completed.LastProcessedIndex);
            Assert.Equal(1, completed.SkippedDuplicates);
            Assert.Equal(0, completed.RemainingEnrichmentCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [PostgresFact]
    public async Task LegacyNoKeyLookup_UsesIndexableBoundedSpatialPredicate()
    {
        var user = await fixture.CreateUserAsync();
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.AddRange(Enumerable.Range(0, 500)
                .Select(index => Location(user.Id, null, index)));
            await seed.SaveChangesAsync();
        }

        var commands = new List<string>();
        await using var context = fixture.CreateContext(new LegacyLookupRecorder(commands));
        var batch = new List<Location> { Location(user.Id, null, 1_000) };

        var result = await LocationImportDeduplicator.FilterAsync(
            context, batch, new HashSet<Guid>(), user.Id, NullLogger.Instance, CancellationToken.None);

        Assert.Single(result.ToInsert);
        await using (var grow = fixture.CreateContext())
        {
            grow.Locations.AddRange(Enumerable.Range(500, 4_500)
                .Select(index => Location(user.Id, null, index)));
            await grow.SaveChangesAsync();
        }
        _ = await LocationImportDeduplicator.FilterAsync(
            context, batch, new HashSet<Guid>(), user.Id, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, sql =>
        {
            Assert.Contains("ST_DWithin", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ST_Distance", sql, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(commands[0], commands[1]);
        Assert.Empty(context.ChangeTracker.Entries<Location>());
    }

    [PostgresFact]
    public async Task LegacyNoKeyLookup_PostgresHasUsableTimeAndSpatialIndexes()
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT index_class.relname, index_metadata.indisvalid, index_metadata.indisready,
                   access_method.amname, pg_get_indexdef(index_class.oid)
            FROM pg_catalog.pg_index AS index_metadata
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = index_metadata.indrelid
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = index_metadata.indexrelid
            JOIN pg_catalog.pg_am AS access_method ON access_method.oid = index_class.relam
            WHERE table_class.relname = 'Locations'
              AND table_class.relnamespace = pg_catalog.to_regnamespace(current_schema())
              AND index_class.relname IN ('IX_Location_UserId_Timestamp', 'IX_Location_Coordinates')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new Dictionary<string, (bool Valid, bool Ready, string Method, string Definition)>();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0),
                (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4)));
        }

        var timeIndex = indexes["IX_Location_UserId_Timestamp"];
        Assert.True(timeIndex.Valid);
        Assert.True(timeIndex.Ready);
        Assert.Equal("btree", timeIndex.Method);
        Assert.Contains("(\"UserId\", \"Timestamp\")", timeIndex.Definition, StringComparison.Ordinal);

        var spatialIndex = indexes["IX_Location_Coordinates"];
        Assert.True(spatialIndex.Valid);
        Assert.True(spatialIndex.Ready);
        Assert.Equal("gist", spatialIndex.Method);
        Assert.Contains("(\"Coordinates\")", spatialIndex.Definition, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ConcurrentNoKeyReplay_ConvergesToOnePersistedLocation()
    {
        var user = await fixture.CreateUserAsync();
        var paths = Enumerable.Range(0, 2)
            .Select(_ => Path.Combine(ImportStaging.LegacyDirectory, $"no-key-replay-{Guid.NewGuid():N}.csv"))
            .ToArray();
        try
        {
            foreach (var path in paths)
                await File.WriteAllTextAsync(path,
                    "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,2026-08-25T00:00:00Z\r\n");
            int[] importIds;
            await using (var seed = fixture.CreateContext())
            {
                var imports = paths.Select(path => new LocationImport
                {
                    UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                    Status = ImportStatus.InProgress, ExecutionEpoch = 7,
                    TotalRecords = 0, LastProcessedIndex = 0
                }).ToArray();
                seed.LocationImports.AddRange(imports);
                await seed.SaveChangesAsync();
                importIds = imports.Select(item => item.Id).ToArray();
            }
            var service = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture),
                new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());

            var outcomes = await Task.WhenAll(importIds.Select(id =>
                service.ProcessImportExecution(id, 7, CancellationToken.None)));

            Assert.All(outcomes, outcome => Assert.Equal(LocationImportExecutionOutcome.Completed, outcome));
            await using var verification = fixture.CreateContext();
            Assert.Equal(1, await verification.Locations.CountAsync(item => item.UserId == user.Id));
            var completedImports = await verification.LocationImports
                .Where(item => importIds.Contains(item.Id)).ToListAsync();
            Assert.Equal(2, completedImports.Sum(item => item.LastProcessedIndex));
            Assert.Equal(1, completedImports.Sum(item => item.SkippedDuplicates));
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
        }
    }

    [PostgresFact]
    public async Task KeyedBatches_QueryOnlyBatchKeysAndBoundTrackingAsHistoryGrows()
    {
        var user = await fixture.CreateUserAsync();
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.AddRange(Enumerable.Range(0, 500)
                .Select(index => Location(user.Id, Guid.NewGuid(), index)));
            await seed.SaveChangesAsync();
        }

        var keyedLookups = new List<CommandShape>();
        var trackerSizes = new List<int>();
        for (var batchNumber = 0; batchNumber < 4; batchNumber++)
        {
            var recorder = new KeyLookupRecorder(keyedLookups);
            await using var context = fixture.CreateContext(recorder);
            var batch = Enumerable.Range(0, 50)
                .Select(index => Location(user.Id, Guid.NewGuid(), 1_000 + batchNumber * 50 + index))
                .ToList();
            var keys = batch.Select(item => item.IdempotencyKey!.Value).ToHashSet();

            var (toInsert, skipped) = await LocationImportDeduplicator.FilterAsync(
                context, batch, keys, user.Id, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, skipped);
            Assert.Equal(50, toInsert.Count);
            await LocationImportDeduplicator.InsertAsync(context, toInsert, user.Id, CancellationToken.None);
            trackerSizes.Add(context.ChangeTracker.Entries<Location>().Count());
        }

        Assert.Equal(4, keyedLookups.Count);
        Assert.All(keyedLookups, shape =>
        {
            Assert.Contains("IdempotencyKey", shape.Sql, StringComparison.Ordinal);
            Assert.True(shape.Sql.Contains("ANY", StringComparison.OrdinalIgnoreCase) ||
                shape.Sql.Contains("IN", StringComparison.OrdinalIgnoreCase), shape.Sql);
            Assert.Equal(50, shape.KeyCount);
        });
        Assert.All(trackerSizes, size => Assert.InRange(size, 0, 50));
    }

    private static Location Location(string userId, Guid? key, int seconds) => new()
    {
        UserId = userId,
        IdempotencyKey = key,
        Timestamp = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
        LocalTimestamp = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
        TimeZoneId = "UTC",
        Coordinates = new GeoPoint(22.2, 40.1) { SRID = 4326 }
    };

    private sealed record CommandShape(string Sql, int KeyCount);

    private sealed class KeyLookupRecorder(List<CommandShape> shapes) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            if (!command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal) ||
                !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;
            var parameterKeys = command.Parameters.Cast<DbParameter>()
                .SelectMany(parameter => parameter.Value switch
                {
                    Guid key => [key],
                    IEnumerable<Guid> keys => keys,
                    _ => []
                }).Count();
            var literalKeys = Regex.Matches(command.CommandText,
                "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Count;
            shapes.Add(new(command.CommandText, parameterKeys + literalKeys));
        }
    }

    private sealed class LegacyLookupRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Coordinates", StringComparison.Ordinal) &&
                command.CommandText.Contains("Timestamp", StringComparison.Ordinal) &&
                command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture, IInterceptor? interceptor = null,
        bool disableAutoSavepoints = false)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var context = interceptor is null ? fixture.CreateContext() : fixture.CreateContext(interceptor);
            context.Database.AutoSavepointsEnabled = !disableAutoSavepoints;
            return context;
        }
    }

    private sealed class KeyPrecheckInterceptor(Func<Task> afterPrecheck) : DbCommandInterceptor
    {
        private int invoked;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal) &&
                command.CommandText.Contains("Locations", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref invoked, 1) == 0)
                await afterPrecheck();
            return result;
        }
    }
}
