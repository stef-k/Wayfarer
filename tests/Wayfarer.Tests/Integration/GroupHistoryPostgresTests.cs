using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Integration;

/// <summary>Guarded PG18 evidence for real history selection, sampling and complete date-action commands.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class GroupHistoryPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Fixed date and distinct captures avoid unspecified latest ties in provider fixtures.</summary>
    private static readonly DateTime CaptureDay = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One/eight authorized subjects keep the measured baseline's per-subject SQL slope.</summary>
    [PostgresTheory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task DateHistoryCommandCountsStayAtBaseline(int subjectCount)
    {
        var (groupId, users, token) = await SeedAsync(subjectCount);
        Exception? primary = null;
        try
        {
            await using var seed = fixture.CreateContext();
            var activity = await seed.ActivityTypes.OrderBy(a => a.Id).FirstAsync();
            var locations = users.Take(subjectCount).SelectMany(user => new[]
            {
                Marker(user.Id, 1), Marker(user.Id, 2)
            }).ToArray();
            foreach (var location in locations) location.ActivityTypeId = activity.Id;
            seed.Locations.AddRange(locations);
            seed.Locations.Add(Marker(users[^1].Id, 3));
            await seed.SaveChangesAsync();
            var expectedIds = locations.Select(l => l.Id).Order().ToArray();
            foreach (var mobile in new[] { false, true })
            {
                var request = Request();
                request.UserIds = users.Select(u => u.Id).ToList(); // The newer outside source cannot gain authority.
                request.PageSize = mobile ? locations.Length - 1 : 1;
                var recorder = new HistoryCommands();
                var first = await QueryAsync(groupId, users[0].Id, token, mobile, request, recorder);
                Assert.Equal(locations.Length, first.GetProperty("totalItems").GetInt32());
                var rows = first.GetProperty("results").EnumerateArray().ToArray();
                Assert.All(rows, row =>
                {
                    Assert.Contains(row.GetProperty("id").GetInt32(), expectedIds);
                    Assert.Equal(activity.Id, row.GetProperty("activityTypeId").GetInt32());
                    Assert.Equal(activity.Name, row.GetProperty("activityType").GetString());
                });
                AssertCommands(recorder, subjectCount, mobile, "first");
                if (!mobile)
                {
                    Assert.Equal(expectedIds, Ids(first).Order());
                    Assert.False(first.TryGetProperty("pageSize", out _));
                    continue;
                }
                Assert.True(first.GetProperty("hasMore").GetBoolean());
                request.ContinuationToken = first.GetProperty("nextPageToken").GetString();
                recorder = new HistoryCommands();
                var last = await QueryAsync(groupId, users[0].Id, token, true, request, recorder);
                Assert.Single(Ids(last));
                Assert.Equal(expectedIds, Ids(first).Concat(Ids(last)).Order());
                Assert.False(last.GetProperty("hasMore").GetBoolean());
                AssertCommands(recorder, subjectCount, true, "continuation");
            }
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await CleanupAsync(groupId, users, primary); }
    }

    /// <summary>Dense month sampling precedes literal bbox filtering without backfill or relabeling.</summary>
    [PostgresFact]
    public async Task DenseMonthSamplesBeforeBboxAndPagesSelectedMarkers()
    {
        var (groupId, users, token) = await SeedAsync(1);
        Exception? primary = null;
        try
        {
            await using var seed = fixture.CreateContext();
            var locations = Enumerable.Range(0, 3001).Select(index =>
                Marker(users[0].Id, index, index >= 2999 ? 10.002 : 10.001)).ToArray();
            seed.Locations.AddRange(locations);
            await seed.SaveChangesAsync();
            // Verify the spatial premise through PostGIS, outside any measured action.
            await using var connection = fixture.CreateConnection();
            await connection.OpenAsync();
            await using var cellCount = connection.CreateCommand();
            cellCount.CommandText = "SELECT COUNT(DISTINCT ST_GeoHash(\"Coordinates\"::geometry, 5)) FROM \"Locations\" WHERE \"UserId\" = @userId";
            cellCount.Parameters.AddWithValue("userId", users[0].Id);
            Assert.Equal(1L, await cellCount.ExecuteScalarAsync());
            var expected = locations.TakeLast(5).Select(l => l.Id).Order().ToArray();
            var request = Request();
            request.DateType = "month";
            request.PageSize = 3;
            var web = await QueryAsync(groupId, users[0].Id, token, false, request);
            Assert.Equal(5, web.GetProperty("totalItems").GetInt32());
            Assert.Equal(expected, Ids(web).Order());
            var first = await QueryAsync(groupId, users[0].Id, token, true, request);
            Assert.Equal(5, first.GetProperty("totalItems").GetInt32());
            Assert.Equal(3, first.GetProperty("returnedItems").GetInt32());
            Assert.True(first.GetProperty("hasMore").GetBoolean());
            Assert.True(first.GetProperty("isTruncated").GetBoolean());
            request.ContinuationToken = first.GetProperty("nextPageToken").GetString();
            var last = await QueryAsync(groupId, users[0].Id, token, true, request);
            Assert.Equal(expected, Ids(first).Concat(Ids(last)).Order());
            Assert.Equal(5, last.GetProperty("totalItems").GetInt32());
            Assert.Equal(2, last.GetProperty("returnedItems").GetInt32());
            Assert.False(last.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, last.GetProperty("nextPageToken").ValueKind);
            Assert.True(last.GetProperty("isTruncated").GetBoolean());
            request.ContinuationToken = null;
            request.PageSize = 5;
            var all = await QueryAsync(groupId, users[0].Id, token, true, request);
            Assert.Equal(5, Ids(all).Length);
            Assert.False(all.GetProperty("hasMore").GetBoolean());
            Assert.False(all.GetProperty("isTruncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, all.GetProperty("nextPageToken").ValueKind);
            request.MaxLng = 10.0015;
            foreach (var mobile in new[] { false, true })
            {
                var narrow = await QueryAsync(groupId, users[0].Id, token, mobile, request);
                Assert.Equal(3, narrow.GetProperty("totalItems").GetInt32());
                Assert.Equal(expected.Take(3), Ids(narrow).Order());
                Assert.All(narrow.GetProperty("results").EnumerateArray(),
                    row => Assert.False(row.GetProperty("isLatestLocation").GetBoolean()));
            }
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await CleanupAsync(groupId, users, primary); }
    }

    /// <summary>No-year viewport fallback retains expanded-box sampling and underlying row totals.</summary>
    [PostgresFact]
    public async Task ViewportFallbackKeepsExpandedBoxAndUnderlyingCount()
    {
        var (groupId, users, token) = await SeedAsync(1);
        Exception? primary = null;
        try
        {
            await using var seed = fixture.CreateContext();
            var locations = Enumerable.Range(0, 401).Select(index =>
                Marker(users[0].Id, index, index == 400 ? 10.05 : 10.001)).ToArray();
            seed.Locations.AddRange(locations);
            await seed.SaveChangesAsync();
            var request = Request();
            request.Year = null;
            request.DateType = "month";
            request.MaxLng = 10.01;
            request.ZoomLevel = 3;
            foreach (var mobile in new[] { false, true })
            {
                var result = await QueryAsync(groupId, users[0].Id, token, mobile, request);
                Assert.Equal(401, result.GetProperty("totalItems").GetInt32());
                Assert.Equal(new[] { locations[^1].Id }, Ids(result));
                var row = Assert.Single(result.GetProperty("results").EnumerateArray());
                Assert.True(row.GetProperty("coordinates").GetProperty("longitude").GetDouble() > request.MaxLng);
                Assert.False(row.GetProperty("isLatestLocation").GetBoolean());
                if (mobile)
                {
                    Assert.Equal(1, result.GetProperty("returnedItems").GetInt32());
                    Assert.False(result.GetProperty("hasMore").GetBoolean());
                    Assert.False(result.GetProperty("isTruncated").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, result.GetProperty("nextPageToken").ValueKind);
                }
            }
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await CleanupAsync(groupId, users, primary); }
    }

    /// <summary>Each action gets a fresh context/controller/request identity; setup is never counted.</summary>
    private async Task<JsonElement> QueryAsync(Guid groupId, string callerId, string token, bool mobile,
        GroupLocationsQueryRequest request, HistoryCommands? recorder = null)
    {
        await using var db = recorder is null ? fixture.CreateContext() : fixture.CreateContext(recorder);
        var http = new DefaultHttpContext();
        var timeline = new GroupTimelineService(db, new LocationService(db), new ConfigurationBuilder().Build());
        IActionResult response;
        if (mobile)
        {
            http.Request.Headers.Authorization = $"Bearer {token}";
            var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = http }, db,
                NullLogger<MobileCurrentUserAccessor>.Instance);
            var controller = new MobileGroupsController(db, NullLogger<BaseApiController>.Instance, accessor,
                new UserColorService(), timeline) { ControllerContext = new ControllerContext { HttpContext = http } };
            response = await controller.Query(groupId, request, default);
        }
        else
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, callerId)], "cookie"));
            var controller = new GroupsController(db, new GroupService(db), NullLogger<GroupsController>.Instance,
                new LocationService(db), new SseService(), timeline)
                { ControllerContext = new ControllerContext { HttpContext = http } };
            response = await controller.Query(groupId, request, default);
        }
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(response).Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new PointJsonConverter() } });
    }

    /// <summary>Measured baseline counts cover access/settings, history and successful bearer identity reads.</summary>
    private void AssertCommands(HistoryCommands recorder, int subjects, bool mobile, string page)
    {
        output.WriteLine($"subjects={subjects} adapter={(mobile ? "Mobile" : "Web")} page={page} total={recorder.Shapes.Count} shapes={string.Join(",", recorder.Shapes.GroupBy(s => s).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        Assert.Equal(3 + 3 * subjects + (mobile ? 2 : 0), recorder.Shapes.Count);
        Assert.Equal(subjects, recorder.Shapes.Count(s => s == "location-count"));
        Assert.Equal(subjects, recorder.Shapes.Count(s => s == "location-select"));
        Assert.Equal(subjects + 1, recorder.Shapes.Count(s => s == "settings"));
        Assert.Equal(1, recorder.Shapes.Count(s => s == "group"));
        Assert.Equal(1, recorder.Shapes.Count(s => s == "members"));
        Assert.Equal(mobile ? 2 : 0, recorder.Shapes.Count(s => s == "identity"));
    }

    /// <summary>Fixture owns users; each test owns one group, credential and only those users' rows.</summary>
    private async Task<(Guid GroupId, List<ApplicationUser> Users, string Token)> SeedAsync(int subjects)
    {
        fixture.RequireAvailable();
        var users = new List<ApplicationUser>();
        for (var index = 0; index <= subjects; index++) users.Add(await fixture.CreateUserAsync());
        await using var db = fixture.CreateContext();
        var group = new Group { Id = Guid.NewGuid(), Name = "History fixture", GroupType = "Family", OwnerUserId = users[0].Id };
        var token = Guid.NewGuid().ToString();
        db.Groups.Add(group);
        db.GroupMembers.AddRange(users.Take(subjects).Select(user => new GroupMember
            { GroupId = group.Id, UserId = user.Id, Role = user == users[0] ? GroupMember.Roles.Owner : GroupMember.Roles.Member }));
        db.ApiTokens.Add(new ApiToken { UserId = users[0].Id, User = (await db.Users.FindAsync(users[0].Id))!,
            Name = "history-fixture", TokenHash = ApiTokenService.HashToken(token) });
        await db.SaveChangesAsync();
        return (group.Id, users, token);
    }

    /// <summary>Cleanup remains owned and secondary failures never replace the product assertion.</summary>
    private async Task CleanupAsync(Guid groupId, List<ApplicationUser> users, Exception? primary)
    {
        try
        {
            await using var db = fixture.CreateContext();
            var ids = users.Select(u => u.Id).ToArray();
            await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
            await db.Locations.Where(l => ids.Contains(l.UserId)).ExecuteDeleteAsync();
        }
        catch (Exception cleanup)
        {
            output.WriteLine($"Owned history fixture cleanup failed: {cleanup.GetType().Name}");
            if (primary is null) throw;
        }
    }

    /// <summary>Distinct same-cell captures select the newest five; a common country exercises low zoom.</summary>
    private static Location Marker(string userId, int index, double longitude = 10.001) => new()
    {
        UserId = userId, Coordinates = new Point(longitude, 10.001) { SRID = 4326 }, TimeZoneId = "UTC",
        Timestamp = CaptureDay.AddSeconds(index), LocalTimestamp = CaptureDay.AddSeconds(index), Country = "fixture"
    };

    /// <summary>Requests keep the explicit date path unless a test deliberately removes the year.</summary>
    private static GroupLocationsQueryRequest Request() => new()
    {
        MinLng = 10, MinLat = 10, MaxLng = 11, MaxLat = 11,
        DateType = "day", Year = CaptureDay.Year, Month = CaptureDay.Month, Day = CaptureDay.Day
    };

    /// <summary>Dense sampling has no outer order; compare marker sets where appropriate.</summary>
    private static int[] Ids(JsonElement payload) => payload.GetProperty("results").EnumerateArray()
        .Select(row => row.GetProperty("id").GetInt32()).ToArray();

    /// <summary>Per-context EF command shapes omit SQL values and cover sync/async readers and scalars.</summary>
    private sealed class HistoryCommands : DbCommandInterceptor
    {
        /// <summary>Only date-history actions are counted; viewport direct ADO.NET counts are excluded.</summary>
        public List<string> Shapes { get; } = [];

        /// <summary>Records synchronous reader execution.</summary>
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) { Record(command); return result; }

        /// <summary>Records asynchronous reader execution, including date FromSqlRaw sampling.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }

        /// <summary>Records synchronous EF scalar execution.</summary>
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result) { Record(command); return result; }

        /// <summary>Records asynchronous EF scalar execution.</summary>
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }

        /// <summary>Classifies operation/table shape without retaining SQL or private parameter values.</summary>
        private void Record(DbCommand command)
        {
            var sql = command.CommandText;
            Shapes.Add(sql.Contains("\"Locations\"", StringComparison.Ordinal) ? (sql.Contains("count(*)", StringComparison.OrdinalIgnoreCase) ? "location-count" : "location-select")
                : sql.Contains("\"ApplicationSettings\"", StringComparison.Ordinal) ? "settings"
                : sql.Contains("\"GroupMembers\"", StringComparison.Ordinal) ? "members"
                : sql.Contains("\"Groups\"", StringComparison.Ordinal) ? "group"
                : sql.Contains("\"ApiTokens\"", StringComparison.Ordinal) ? "identity" : "other");
        }
    }
}
