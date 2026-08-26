using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using GeoPoint = NetTopologySuite.Geometries.Point;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves keyed import work remains batch-shaped as persisted history grows.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportBoundedPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task KeyedExternalWriterAfterPrecheck_RetainsWinnerAndPersistsRestOfBatch()
    {
        var user = await fixture.CreateUserAsync();
        var conflictingKey = Guid.NewGuid();
        var otherKey = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"keyed-conflict-{Guid.NewGuid():N}.csv");
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
            var service = new LocationImportService(new FixtureFactory(fixture, interceptor, disableAutoSavepoints: true),
                new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());

            await service.ProcessImport(importId, CancellationToken.None);

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
    public async Task LegacyNoKeyLookup_PostgresPlansCanUseTimeAndSpatialIndexes()
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SET enable_seqscan=off";
            await settings.ExecuteNonQueryAsync();
        }

        var timePlan = await ExplainAsync(connection, """
            SELECT 1 FROM "Locations"
            WHERE "UserId" = 'plan-owner'
              AND "Timestamp" BETWEEN TIMESTAMPTZ '2026-08-25T00:00:00Z'
                  AND TIMESTAMPTZ '2026-08-25T00:00:02Z'
            """);
        var spatialPlan = await ExplainAsync(connection, """
            SELECT 1 FROM "Locations"
            WHERE ST_DWithin("Coordinates",
                ST_SetSRID(ST_MakePoint(22.2, 40.1), 4326)::geography, 10)
            """);

        Assert.Contains("IX_Location_UserId_Timestamp", timePlan, StringComparison.Ordinal);
        Assert.Contains("IX_Location_Coordinates", spatialPlan, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ConcurrentNoKeyReplay_ConvergesToOnePersistedLocation()
    {
        var user = await fixture.CreateUserAsync();
        var paths = Enumerable.Range(0, 2)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"no-key-replay-{Guid.NewGuid():N}.csv"))
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
            var service = new LocationImportService(new FixtureFactory(fixture),
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

    private static async Task<string> ExplainAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (COSTS OFF) {sql}";
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
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
