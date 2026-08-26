using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using GeoPoint = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves keyed import work remains batch-shaped as persisted history grows.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportBoundedPostgresTests(PostgresImportTestFixture fixture)
{
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
        var sql = Assert.Single(commands);
        Assert.Contains("ST_DWithin", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ST_Distance", sql, StringComparison.OrdinalIgnoreCase);
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
}
