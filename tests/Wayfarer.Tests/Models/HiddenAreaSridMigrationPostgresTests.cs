using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Models;

/// <summary>Repairs legacy drawing metadata through real EF history on the disposable database only.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class HiddenAreaSridMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260905095140_AddLocationProviderAddressLine1";
    private const string RepairMigration = "20260922182107_RepairHiddenAreaSrid";

    /// <summary>Reproduces both endpoint failures, then proves repaired privacy and unchanged owner history.</summary>
    [PostgresFact]
    public async Task Migration_RepairsLegacyCoordinates_AndRestoresPublicEndpoints()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        Exception? primary = null;
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            user.IsTimelinePublic = true;
            user.PublicTimelineTimeThreshold = "now";
            db.Users.Update(user);
            var legacy = Area(user.Id, 0);
            var correct = Area(user.Id, 4326, 10);
            db.HiddenAreas.AddRange(legacy, correct);
            var visible = Row(user.Id, 23, "Visible", DateTime.UtcNow.AddDays(-2));
            var hidden = Row(user.Id, 25, "Hidden", DateTime.UtcNow.AddDays(-1));
            db.Locations.AddRange(visible, hidden);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.HiddenAreas.AsNoTracking().Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).ToArrayAsync();
            // Query ST_SRID in PostgreSQL: NTS deserializes an unspecified EWKB SRID as -1.
            Assert.Equal(new[] { 0, 4326 }, await db.HiddenAreas.Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).Select(a => a.Area.SRID).ToArrayAsync());
            var controller = Controller(db);

            var pointsError = await Assert.ThrowsAsync<PostgresException>(
                () => controller.GetPublicTimeline(Request(user.UserName!)));
            var statsError = await Assert.ThrowsAsync<PostgresException>(
                () => controller.GetPublicStats(user.UserName!));
            Assert.Contains("mixed SRID", pointsError.MessageText);
            Assert.Contains("mixed SRID", statsError.MessageText);

            await migrator.MigrateAsync(RepairMigration);

            var after = await db.HiddenAreas.AsNoTracking().Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).ToArrayAsync();
            Assert.All(after, a => Assert.Equal(4326, a.Area.SRID));
            Assert.Equal(before[0].Area.AsBinary(), after[0].Area.AsBinary());
            Assert.Equal(before[1].Area.AsBinary(), after[1].Area.AsBinary());
            await AssertRepairedEndpointsAsync(db, user, visible);

            // Down removes history only; it must not undo correct polygon metadata.
            await migrator.MigrateAsync(PreviousMigration);
            Assert.All(await db.HiddenAreas.AsNoTracking().Where(a => a.UserId == user.Id).ToArrayAsync(),
                a => Assert.Equal(4326, a.Area.SRID));
        }
        catch (Exception failure) { primary = failure; }
        finally { await RestoreAsync(db, user.Id, primary); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Unexpected CRS aborts the real migration, preserving every polygon and migration history.</summary>
    [PostgresFact]
    public async Task Migration_UnexpectedSrid_DoesNotPartiallyRepair()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        Exception? primary = null;
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            db.HiddenAreas.AddRange(Area(user.Id, 0), Area(user.Id, 4326), Area(user.Id, 3857));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.HiddenAreas.AsNoTracking().Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).ToArrayAsync();

            var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(RepairMigration));

            Assert.Contains("unexpected SRIDs", error.MessageText);
            Assert.Contains("verify the source coordinate system", error.Hint);
            Assert.DoesNotContain(RepairMigration, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(new[] { 0, 4326, 3857 }, await db.HiddenAreas.Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).Select(a => a.Area.SRID).ToArrayAsync());
            var after = await db.HiddenAreas.AsNoTracking().Where(a => a.UserId == user.Id)
                .OrderBy(a => a.Id).ToArrayAsync();
            Assert.Equal(before.Select(a => a.Area.SRID), after.Select(a => a.Area.SRID));
            for (var i = 0; i < before.Length; i++)
                Assert.Equal(before[i].Area.AsBinary(), after[i].Area.AsBinary());
        }
        catch (Exception failure) { primary = failure; }
        finally { await RestoreAsync(db, user.Id, primary); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Uses the production controllers/services and checks the point envelope, public and private counts.</summary>
    private static async Task AssertRepairedEndpointsAsync(ApplicationDbContext db, ApplicationUser user, Location visible)
    {
        var controller = Controller(db);
        var result = Assert.IsType<OkObjectResult>(await controller.GetPublicTimeline(Request(user.UserName!)));
        var envelope = result.Value!;
        Assert.True((bool)envelope.GetType().GetProperty("Success")!.GetValue(envelope)!);
        var points = (IEnumerable<PublicLocationDto>)envelope.GetType().GetProperty("Data")!.GetValue(envelope)!;
        var point = Assert.Single(points);
        Assert.Equal(visible.Id, point.Id);
        Assert.True(point.IsLatestLocation);
        var summary = Assert.IsType<UserLocationStatsDto>(
            Assert.IsType<OkObjectResult>(await controller.GetPublicStats(user.UserName!)).Value);
        Assert.Equal((1, 1, 1, 1),
            (summary.TotalLocations, summary.CountriesVisited, summary.RegionsVisited, summary.CitiesVisited));
        var privateSummary = await new LocationStatsService(db).GetStatsForUserAsync(user.Id);
        Assert.Equal((2, 2, 2, 2),
            (privateSummary.TotalLocations, privateSummary.CountriesVisited, privateSummary.RegionsVisited, privateSummary.CitiesVisited));
    }

    /// <summary>Removes only this scenario's owner and restores latest history, retaining any primary failure.</summary>
    private static async Task RestoreAsync(ApplicationDbContext db, string userId, Exception? primary)
    {
        try
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
            await db.GetService<IMigrator>().MigrateAsync();
        }
        catch when (primary is not null)
        {
            primary.Data["PostgresMigrationRestore"] = "Latest migration restoration also failed.";
        }
    }

    /// <summary>Builds a polygon around the hidden point, or a distant already-correct polygon.</summary>
    private static HiddenArea Area(string userId, int srid, int offset = 0)
    {
        var polygon = (Polygon)new WKTReader().Read("POLYGON ((24 39, 26 39, 26 41, 24 41, 24 39))");
        if (offset != 0)
            polygon = (Polygon)NetTopologySuite.Geometries.Utilities.AffineTransformation
                .TranslationInstance(offset, offset).Transform(polygon);
        polygon.SRID = srid;
        return new HiddenArea { UserId = userId, Name = "SRID regression", Area = polygon };
    }

    /// <summary>Distinct labels make private-history leakage observable through each aggregate count.</summary>
    private static Location Row(string userId, double longitude, string label, DateTime time) => new()
    {
        UserId = userId, Timestamp = time, LocalTimestamp = time, TimeZoneId = "Etc/UTC",
        Coordinates = new Point(longitude, 40) { SRID = 4326 }, Country = label, Region = label, Place = label
    };

    /// <summary>Constructs the real controller with its production query consumers.</summary>
    private static UsersTimelineController Controller(ApplicationDbContext db) =>
        new(NullLogger<BaseController>.Instance, db, new LocationService(db), new LocationStatsService(db));

    /// <summary>Includes both visible and hidden history at a zoom that returns individual points.</summary>
    private static LocationFilterRequest Request(string username) => new()
    {
        Username = username, MinLongitude = 20, MaxLongitude = 30,
        MinLatitude = 35, MaxLatitude = 45, ZoomLevel = 12
    };
}
