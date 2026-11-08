using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Uses the normal transactional migrator in the established fixture-owned disposable database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationKeyMigrationPostgresTests(PostgresMigrationTestFixture fixture) : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Null-only upgrade is atomic, stable on repeat/rollback, and usable by export/replay/deletion owners.</summary>
    [PostgresFact]
    public async Task NullOnlyUpgradePreservesEveryOtherValueAndRetriesAfterRollback()
    {
        await using var db = fixture.CreateContext();
        // Fixture initialization has already exercised an empty installation through the complete migration chain.
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Locations.ToListAsync());
        await db.GetService<IMigrator>().MigrateAsync("20261108172801_SharedLocationOriginalClocks");
        var owner = await fixture.CreateUserAsync();
        var rows = new[] { Row(owner.Id, null), Row(owner.Id, null), Row(owner.Id, Guid.Empty), Row(owner.Id, Guid.NewGuid()) };
        db.Locations.AddRange(rows); await db.SaveChangesAsync();
        var before = await ContentAsync(db);
        await using (var failing = fixture.CreateContext(new FailAfterAssignment()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Database.MigrateAsync());
        Assert.Equal(before, await ContentAsync(db));
        Assert.Equal(2, await db.Locations.CountAsync(row => row.IdempotencyKey == null));
        Assert.Contains("20261108172840_AssignStoredLocationKeys", await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var assigned = await db.Locations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        var keys = assigned.Select(row => row.IdempotencyKey).ToArray();
        Assert.All(keys, key => Assert.NotNull(key)); Assert.Equal(4, keys.Distinct().Count());
        Assert.Equal(Guid.Empty, keys[2]); Assert.Equal(rows[3].IdempotencyKey, keys[3]);
        Assert.Equal(before, await ContentAsync(db));
        await db.Database.MigrateAsync();
        Assert.Equal(keys, await KeysAsync(db));
        // Down removes only migration history; it must never erase identities already published to clients.
        await db.GetService<IMigrator>().MigrateAsync("20261108172801_SharedLocationOriginalClocks");
        Assert.Equal(keys, await KeysAsync(db));
        await db.Database.MigrateAsync(); Assert.Equal(keys, await KeysAsync(db));

        var controller = new LocationExportController(db) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.Id)], "test")) } } };
        foreach (var csv in new[] { false, true })
        {
            var file = Assert.IsType<FileStreamResult>(csv ? await controller.Csv() : await controller.GeoJson());
            await using var stream = file.FileStream;
            ILocationDataParser parser = csv ? new CsvLocationParser(NullLogger<CsvLocationParser>.Instance) : new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
            var published = new List<Guid?>(); await foreach (var row in parser.ParseAsync(stream, owner.Id)) published.Add(row.IdempotencyKey);
            Assert.Equal(keys.Order(), published.Order());
        }
        await using var current = fixture.CreateContext();
        using var http = new HttpClient(new NoNetwork());
        var reverse = new ReverseGeocodingService(http, NullLogger<BaseApiController>.Instance);
        var replay = Row(owner.Id, keys[0]); replay.Notes = "must not overwrite";
        var winner = await ApiLocationCreation.PersistAsync(current, reverse, replay, CancellationToken.None);
        Assert.False(winner.Created); Assert.Equal(rows[0].Id, winner.Location.Id); Assert.Equal(rows[0].Notes, winner.Location.Notes);
        await LocationDeletionFence.DeleteAsync(current, owner.Id, [rows[0].Id], null, CancellationToken.None);
        await Assert.ThrowsAsync<LocationDeletedException>(() => ApiLocationCreation.PersistAsync(current, reverse, replay, CancellationToken.None));
        var laterAnonymous = Row(owner.Id, null); current.Locations.Add(laterAnonymous); await current.SaveChangesAsync();
        await current.Database.MigrateAsync();
        Assert.Null((await current.Locations.AsNoTracking().SingleAsync(row => row.Id == laterAnonymous.Id)).IdempotencyKey);
    }

    /// <summary>Compares every stored field, including derived and authority fields, without a hand-maintained preservation subset.</summary>
    private static async Task<string> ContentAsync(ApplicationDbContext db) => await db.Database.SqlQuery<string>($"""
        SELECT jsonb_agg(to_jsonb(l) - 'IdempotencyKey' ORDER BY "Id")::text AS "Value" FROM "Locations" l
        """).SingleAsync();
    private static Task<Guid?[]> KeysAsync(ApplicationDbContext db) => db.Locations.AsNoTracking().OrderBy(row => row.Id).Select(row => row.IdempotencyKey).ToArrayAsync();

    /// <summary>Nearby distinct history, existing zero identity and independent original facts exercise real stored values.</summary>
    private static Location Row(string owner, Guid? key) => new() { UserId = owner, IdempotencyKey = key,
        Coordinates = new(23.73, 37.98) { SRID = 4326 }, TimeZoneId = "Europe/Athens", Timestamp = DateTime.UnixEpoch,
        LocalTimestamp = DateTime.UnixEpoch, Notes = "<p>History / Αθήνα</p>", Address = "", FullAddress = "independent",
        OriginalLocalTimestamp = "2026-11-08T15:00:00.123456789", OriginalTimeZoneId = "GMT+03:00", OriginalUtcOffsetSeconds = 10800,
        SubmittedAtUtc = DateTimeOffset.UnixEpoch, BatteryLevel = 0, IsCharging = false, DeviceModel = "retained" };

    /// <summary>Fails after assignment executed but before migration commit/history; the ordinary migrator must roll back both.</summary>
    private sealed class FailAfterAssignment : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET \"IdempotencyKey\" = gen_random_uuid()", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic upgrade interruption.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay must not contact a provider.");
    }
}
