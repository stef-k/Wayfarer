using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Semantic matrices execute the production SQL predicate, without duplicating them through HTTP/browser flows.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class HistoryPredicatePostgresTests(PostgresMigrationTestFixture fixture) : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Both fold instants survive; spring gaps supply no events. Capture and viewer agree under the same rules.</summary>
    [PostgresTheory]
    [InlineData("capture", "2025-11-02", "01:00", "02:00", 2)]
    [InlineData("viewer", "2025-11-02", "01:00", "02:00", 2)]
    [InlineData("capture", "2025-03-09", "02:00", "03:00", 0)]
    [InlineData("viewer", "2025-03-09", "02:00", "03:00", 0)]
    public async Task DstMembershipUsesEveryEventInstant(string basis, string date, string from, string until, int expected)
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.AddRange(new[] { "2025-11-02T05:30:00Z", "2025-11-02T06:30:00Z", "2025-11-02T07:00:00Z", "2025-03-09T06:30:00Z", "2025-03-09T07:30:00Z" }
            .Select(instant => Row(owner.Id, instant, -74, 40.7)));
        await db.SaveChangesAsync();
        var query = HistoryQueryTests.Parse($"?timeBasis={basis}&timeMode=daily&viewerZone=America%2FNew_York&fromDate={date}&throughDate={date}&fromTime={from}&untilTime={until}");
        Assert.Equal(expected, (await SelectAsync(db, owner.Id, query)).Length);
    }

    /// <summary>UTC and wall boundaries stay half-open down to a microsecond; historical offsets include seconds.</summary>
    [PostgresFact]
    public async Task MicrosecondContinuousAndHistoricalNonHourOffsetsAreExact()
    {
        var owner = await fixture.CreateUserAsync(); await using var db = fixture.CreateContext();
        var rows = new[] { "2025-10-10T13:00:00Z", "2025-10-11T03:00:00Z", "2025-10-12T16:59:59.999999Z", "2025-10-12T17:00:00Z", "2025-10-09T16:00:00Z" }
            .Select(value => Row(owner.Id, value, 139.76, 35.68)).ToArray();
        db.AddRange(rows); await db.SaveChangesAsync();
        var daily = HistoryQueryTests.Parse("?timeBasis=capture&timeMode=daily&viewerZone=UTC&fromDate=2025-10-10&throughDate=2025-10-12&fromTime=22:00&untilTime=02:00");
        Assert.Equal(new[] { rows[0].Id, rows[2].Id }.Order(), (await SelectAsync(db, owner.Id, daily)).Order());
        var interval = HistoryQueryTests.Parse("?timeBasis=capture&timeMode=interval&viewerZone=UTC&fromLocal=2025-10-10T22:00:00&untilLocal=2025-10-13T02:00:00");
        Assert.Equal(3, (await SelectAsync(db, owner.Id, interval)).Length);
        var utc = HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-10-12T16:59:59.999999Z&endUtc=2025-10-12T17:00:00Z");
        Assert.Equal(rows[2].Id, Assert.Single(await SelectAsync(db, owner.Id, utc)));
        var historical = Row(owner.Id, "1900-01-01T00:00:00Z", 85.324, 27.717);
        db.Add(historical); await db.SaveChangesAsync();
        Assert.Equal(20476, historical.CaptureOffsetSeconds);
        var nonHour = HistoryQueryTests.Parse("?timeBasis=viewer&timeMode=interval&viewerZone=Asia%2FKathmandu&fromLocal=1900-01-01T05:41:16&untilLocal=1900-01-01T05:41:17");
        Assert.Equal(historical.Id, Assert.Single(await SelectAsync(db, owner.Id, nonHour)));
    }

    /// <summary>Literal field-delimited accent-folded text, activity OR/null and exact geography compose by AND.</summary>
    [PostgresFact]
    public async Task TextActivityAndDatelineAreaShareOnePredicate()
    {
        var owner = await fixture.CreateUserAsync(); await using var db = fixture.CreateContext();
        var walking = new ActivityType { Name = "History walking" }; var cycling = new ActivityType { Name = "History cycling" };
        db.AddRange(walking, cycling); await new ActivityCatalog(db).SaveInitialAsync();
        var activities = new[] { walking.Id, cycling.Id };
        var rows = Enumerable.Range(0, 5).Select(_ => Row(owner.Id, "2025-10-10T03:00:00Z", 180, 90)).ToArray();
        rows[0].Notes = "<p>Café <b>sta</b>tion 100% _ \\</p>"; rows[0].ActivityTypeId = activities[0];
        rows[1].Notes = "café"; rows[1].Address = "station"; rows[1].ActivityTypeId = activities[0];
        rows[2].Notes = "CAFÉ station"; rows[2].ActivityTypeId = activities[1];
        rows[3].Notes = "cafe station"; rows[3].Coordinates = new Point(-180, 90) { SRID = 4326 };
        rows[4].Notes = "cafe station"; rows[4].Coordinates = new Point(-170, 89) { SRID = 4326 };
        db.AddRange(rows); await db.SaveChangesAsync();
        var common = $"?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z&q=cafe%20station&activityIds={activities[0]}&includeUnassigned=true&west=170&south=80&east=-170&north=90";
        Assert.Equal(new[] { rows[0].Id, rows[3].Id }.Order(), (await SelectAsync(db, owner.Id, HistoryQueryTests.Parse(common))).Order());
        foreach (var literal in new[] { "%", "_", "\\", "100% _ \\" })
        {
            var query = HistoryQueryTests.Parse(common.Replace("q=cafe%20station", "q=" + Uri.EscapeDataString(literal)));
            Assert.Equal(rows[0].Id, Assert.Single(await SelectAsync(db, owner.Id, query)));
        }
        var westAlias = HistoryQueryTests.Parse(common.Replace("west=170", "west=-180").Replace("east=-170", "east=-179"));
        Assert.Equal(2, (await SelectAsync(db, owner.Id, westAlias)).Length);
        var nullOnly = HistoryQueryTests.Parse(common.Replace("activityIds=" + activities[0] + "&", ""));
        Assert.Equal(rows[3].Id, Assert.Single(await SelectAsync(db, owner.Id, nullOnly)));
        var twoIds = HistoryQueryTests.Parse(common.Replace("activityIds=" + activities[0], "activityIds=" + string.Join(',', activities)));
        Assert.Equal(3, (await SelectAsync(db, owner.Id, twoIds)).Length);
    }

    /// <summary>Known false dimensions exclude unknown time/position; usable event envelopes also prune capture failures.</summary>
    [PostgresFact]
    public async Task ConservativeCandidatesNeverTurnUnknownIntoFalseOrMatches()
    {
        var owner = await fixture.CreateUserAsync(); await using var db = fixture.CreateContext();
        var a = Row(owner.Id, "2025-10-10T03:00:00Z", double.NaN, double.NaN);
        var b = Row(owner.Id, "2020-01-01T03:00:00Z", double.NaN, double.NaN);
        var c = Row(owner.Id, "2025-10-10T03:00:00Z", double.NaN, double.NaN); c.Notes = "known false";
        db.AddRange(a, b, c); await db.SaveChangesAsync();
        var capture = HistoryQueryTests.Parse("?timeBasis=capture&timeMode=days&viewerZone=UTC&fromDate=2025-10-10&throughDate=2025-10-10&q=cafe");
        Assert.Empty(await SelectAsync(db, owner.Id, capture));
        Assert.Equal(a.Id, Assert.Single(await SelectAsync(db, owner.Id, capture, true)));
        // PostgreSQL infinity is unavailable event evidence and cannot be discarded through the envelope.
        await db.Locations.Where(row => row.Id == b.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LocalTimestamp, DateTime.MinValue));
        Assert.Equal(2, (await SelectAsync(db, owner.Id, capture, true)).Length);
    }

    /// <summary>Only the production predicate is under test; projection and routing have their own focused witnesses.</summary>
    private static Task<int[]> SelectAsync(ApplicationDbContext db, string owner, HistoryQuery query, bool candidates = false)
    {
        var predicate = new HistoryPredicate(owner, query, default);
        return db.Database.SqlQueryRaw<int>($"SELECT l.\"Id\" AS \"Value\" FROM \"Locations\" l WHERE {predicate.Where} AND {(candidates ? predicate.Truth + " IS NULL" : predicate.Matches)}", predicate.CopyParameters()).ToArrayAsync();
    }
    private static Location Row(string owner, string instant, double longitude, double latitude) => new()
    {
        UserId = owner, Coordinates = new Point(longitude, latitude) { SRID = 4326 }, TimeZoneId = "UTC", Notes = "café station",
        LocalTimestamp = DateTime.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}
