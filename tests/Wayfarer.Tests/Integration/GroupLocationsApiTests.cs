using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Integration;

public class GroupLocationsApiTests
{
    /// <summary>Fixed capture date keeps explicit history cases independent of the current clock.</summary>
    private static readonly DateTime HistoryDay = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Two authorized subjects retain capture order on Web and server-time/ID order on Mobile.</summary>
    [Fact]
    public async Task HistoryPreservesSubjectBlocksProjectionAndPreBboxLatestFlags()
    {
        using var db = MakeDb();
        var group = SeedHistoryMembers(db);
        db.ActivityTypes.Add(new ActivityType { Id = 7, Name = "Walking" });
        var edge = HistoryLocation("caller", 1, HistoryDay.AddHours(1));
        edge.Timestamp = HistoryDay.AddHours(5);
        edge.Coordinates = new Point(11, 11) { SRID = 4326 };
        edge.ActivityTypeId = 7;
        edge.Notes = "<p>safe</p><script>bad()</script>";
        edge.Address = "Address";
        edge.FullAddress = "Full address";
        edge.ProviderAddressLine1 = "Provider line";
        edge.AddressNumber = "007";
        edge.StreetName = "Street";
        edge.PostCode = "123";
        edge.Place = "Place";
        edge.Region = "Region";
        edge.Country = "Country";
        edge.ResolvedFeatureName = "Feature";
        edge.ResolvedFeatureType = "building";
        edge.ReverseGeocodingProvider = "geoapify";
        edge.ReverseGeocodingStorageMode = "persistent";
        edge.ReverseGeocodedAt = HistoryDay;
        var callerSecond = HistoryLocation("caller", 2, HistoryDay.AddHours(3));
        callerSecond.Timestamp = HistoryDay.AddHours(4);
        var excludedLatest = HistoryLocation("caller", 3, HistoryDay.AddHours(4));
        excludedLatest.Coordinates = new Point(12, 12) { SRID = 4326 };
        var peerFirst = HistoryLocation("peer", 4, HistoryDay.AddHours(2));
        peerFirst.Timestamp = callerSecond.Timestamp;
        var peerLatest = HistoryLocation("peer", 5, HistoryDay.AddHours(5));
        peerLatest.Timestamp = HistoryDay.AddHours(2);
        db.Locations.AddRange(edge, callerSecond, excludedLatest, peerFirst, peerLatest,
            HistoryLocation("caller", 6, HistoryDay.AddDays(-1)));
        await db.SaveChangesAsync();

        // Separate instances prevent the baseline Web action's date mutation from affecting Mobile.
        var webRequest = HistoryRequest(["peer", "caller"]);
        var mobileRequest = HistoryRequest(["peer", "caller"]);
        webRequest.DateType = mobileRequest.DateType = "month";
        var web = HistoryJson(await MakeController(db, "caller").Query(group.Id, webRequest, default));
        var mobile = HistoryJson(await MakeMobileController(db, "caller").Query(group.Id, mobileRequest, default));
        Assert.Equal(new[] { 4, 5, 1, 2 }, HistoryIds(web));
        Assert.Equal(new[] { 1, 4, 2, 5 }, HistoryIds(mobile));
        foreach (var payload in new[] { web, mobile })
        {
            Assert.Equal(4, payload.GetProperty("totalItems").GetInt32());
            var rows = payload.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(5, Assert.Single(rows, r => r.GetProperty("isLatestLocation").GetBoolean()).GetProperty("id").GetInt32());
            var projected = rows.Single(r => r.GetProperty("id").GetInt32() == 1);
            Assert.Equal("Walking", projected.GetProperty("activityType").GetString());
            Assert.Equal(7, projected.GetProperty("activityTypeId").GetInt32());
            Assert.Equal("<p>safe</p>", projected.GetProperty("notes").GetString());
            Assert.Equal("UTC", projected.GetProperty("timezone").GetString());
            Assert.Equal("007", projected.GetProperty("addressNumber").GetString());
            Assert.Equal("Provider line", projected.GetProperty("providerAddressLine1").GetString());
            Assert.Equal("Feature", projected.GetProperty("resolvedFeatureName").GetString());
            Assert.True(projected.GetProperty("isGeoapifyAddress").GetBoolean());
            Assert.Equal(11, projected.GetProperty("coordinates").GetProperty("longitude").GetDouble());
            Assert.Equal("Unknown", rows.Single(r => r.GetProperty("id").GetInt32() == 2).GetProperty("activityType").GetString());
            Assert.Equal(JsonValueKind.Null, projected.GetProperty("clusterId").ValueKind);
            Assert.DoesNotContain("idempotencyKey", projected.ToString());
            Assert.DoesNotContain("deviceModel", projected.ToString());
            Assert.DoesNotContain("reverseGeocodingProvider", projected.ToString());
        }
    }

    /// <summary>History normalization stays adapter-specific, including successful empty selections.</summary>
    [Theory]
    [InlineData(null, 2, 2)]
    [InlineData(new string[0], 2, 2)]
    [InlineData(new[] { "caller", "caller" }, 1, 1)]
    [InlineData(new[] { " caller " }, 0, 1)]
    [InlineData(new[] { " ", "" }, 0, 2)]
    [InlineData(new[] { "outside", "CALLER" }, 0, 0)]
    public async Task HistoryRetainsIdNormalization(string[]? ids, int webCount, int mobileCount)
    {
        using var db = MakeDb();
        var group = SeedHistoryMembers(db);
        db.Locations.AddRange(HistoryLocation("caller", 1, HistoryDay), HistoryLocation("peer", 2, HistoryDay));
        await db.SaveChangesAsync();
        var web = HistoryJson(await MakeController(db, "caller").Query(group.Id, HistoryRequest(ids?.ToList()), default));
        var mobile = HistoryJson(await MakeMobileController(db, "caller").Query(group.Id, HistoryRequest(ids?.ToList()), default));
        Assert.Equal(webCount, web.GetProperty("totalItems").GetInt32());
        Assert.Equal(mobileCount, mobile.GetProperty("totalItems").GetInt32());
        Assert.Equal(mobileCount, mobile.GetProperty("returnedItems").GetInt32());
        Assert.Equal(200, mobile.GetProperty("pageSize").GetInt32());
        Assert.False(mobile.GetProperty("hasMore").GetBoolean());
        Assert.False(mobile.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mobile.GetProperty("nextPageToken").ValueKind);
    }

    /// <summary>Date defaults count authorized subjects; blanks reset while multi-subject gaps only fill.</summary>
    [Theory]
    [InlineData("blank", 1)]
    [InlineData("partial", 2)]
    [InlineData("hidden", 2)]
    public async Task HistoryRetainsEffectiveDateRules(string scenario, int expected)
    {
        using var db = MakeDb();
        var group = SeedHistoryMembers(db);
        var today = DateTime.UtcNow.Date;
        var oldDate = new DateTime(2020, today.Month, today.Day, 0, 0, 0, DateTimeKind.Utc);
        db.Locations.AddRange(HistoryLocation("caller", 1, today), HistoryLocation("caller", 2, oldDate),
            HistoryLocation("peer", 3, oldDate), HistoryLocation("caller", 4, oldDate.AddDays(oldDate.Day == 1 ? 1 : -1)));
        if (scenario == "hidden")
        {
            group.GroupType = "Friends";
            db.GroupMembers.Single(m => m.UserId == "peer").OrgPeerVisibilityAccessDisabled = true;
        }
        await db.SaveChangesAsync();
        foreach (var mobile in new[] { false, true })
        {
            var request = HistoryRequest(scenario == "blank" ? ["caller"] : ["caller", "peer"]);
            request.DateType = scenario == "blank" ? " " : "month";
            request.Year = 2020;
            request.Month = scenario == "partial" ? null : today.Month;
            request.Day = scenario == "partial" ? null : today.Day;
            var result = mobile
                ? await MakeMobileController(db, "caller").Query(group.Id, request, default)
                : await MakeController(db, "caller").Query(group.Id, request, default);
            Assert.Equal(expected, HistoryJson(result).GetProperty("totalItems").GetInt32());
        }
    }

    /// <summary>Web ignores paging fields and returns a day larger than Mobile's shipped 500 cap.</summary>
    [Fact]
    public async Task WebHistoryStaysUnpagedAboveMobileCap()
    {
        using var db = MakeDb();
        var group = SeedHistoryMembers(db);
        db.Locations.AddRange(Enumerable.Range(1, 501).Select(id => HistoryLocation("caller", id, HistoryDay.AddSeconds(id))));
        await db.SaveChangesAsync();
        var webRequest = HistoryRequest(["caller"]);
        webRequest.PageSize = 1;
        webRequest.ContinuationToken = "invalid";
        var web = HistoryJson(await MakeController(db, "caller").Query(group.Id, webRequest, default));
        Assert.Equal(new[] { "results", "totalItems" }, web.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(501, HistoryIds(web).Length);
        var mobileRequest = HistoryRequest(["caller"]);
        mobileRequest.PageSize = 999;
        var mobile = HistoryJson(await MakeMobileController(db, "caller").Query(group.Id, mobileRequest, default));
        Assert.Equal(501, mobile.GetProperty("totalItems").GetInt32());
        Assert.Equal(500, mobile.GetProperty("pageSize").GetInt32());
        Assert.Equal(500, HistoryIds(mobile).Length);
        Assert.True(mobile.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>Absent/archived groups preserve status differences; removed sources never enter history.</summary>
    [Theory]
    [InlineData("missing", 403, 404)]
    [InlineData("archived", 403, 404)]
    [InlineData("removed-caller", 403, 403)]
    [InlineData("removed-source", 200, 200)]
    public async Task HistoryRetainsEligibilityAndStatuses(string state, int webStatus, int mobileStatus)
    {
        using var db = MakeDb();
        var group = SeedHistoryMembers(db);
        db.Locations.AddRange(HistoryLocation("caller", 1, HistoryDay), HistoryLocation("peer", 2, HistoryDay));
        if (state == "archived") group.IsArchived = true;
        if (state.StartsWith("removed"))
            db.GroupMembers.Single(m => m.UserId == (state == "removed-caller" ? "caller" : "peer")).Status = GroupMember.MembershipStatuses.Removed;
        await db.SaveChangesAsync();
        var groupId = state == "missing" ? Guid.NewGuid() : group.Id;
        var web = await MakeController(db, "caller").Query(groupId, HistoryRequest(), default);
        var mobile = await MakeMobileController(db, "caller").Query(groupId, HistoryRequest(), default);
        Assert.Equal(webStatus, web is OkObjectResult ? 200 : Assert.IsType<StatusCodeResult>(web).StatusCode);
        Assert.Equal(mobileStatus, mobile is OkObjectResult ? 200 : mobile is NotFoundResult ? 404 : Assert.IsType<StatusCodeResult>(mobile).StatusCode);
        if (state == "removed-source")
        {
            Assert.Equal(new[] { 1 }, HistoryIds(HistoryJson(web)));
            Assert.Equal(new[] { 1 }, HistoryIds(HistoryJson(mobile)));
        }
    }

    /// <summary>Seeds the same persisted audience for the existing production adapters.</summary>
    private static Group SeedHistoryMembers(ApplicationDbContext db)
    {
        db.Users.AddRange(new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true },
            new ApplicationUser { Id = "peer", UserName = "peer", DisplayName = "Peer", IsActive = true });
        var group = new Group { Id = Guid.NewGuid(), Name = "History", OwnerUserId = "caller", GroupType = "Family" };
        db.Groups.Add(group);
        db.GroupMembers.AddRange(new GroupMember { GroupId = group.Id, UserId = "caller", Role = GroupMember.Roles.Owner },
            new GroupMember { GroupId = group.Id, UserId = "peer" });
        db.SaveChanges();
        return group;
    }

    /// <summary>Explicit bbox and date inputs are copied independently into each adapter call.</summary>
    private static GroupLocationsQueryRequest HistoryRequest(List<string>? ids = null) => new()
    {
        UserIds = ids, DateType = "DaY", Year = HistoryDay.Year, Month = HistoryDay.Month, Day = HistoryDay.Day,
        MinLng = 10, MinLat = 10, MaxLng = 11, MaxLat = 11
    };

    /// <summary>Private entity metadata must remain absent from history DTO serialization.</summary>
    private static Location HistoryLocation(string userId, int id, DateTime captured) => new()
    {
        Id = id, UserId = userId, LocalTimestamp = captured, Timestamp = captured, TimeZoneId = "UTC",
        Coordinates = new Point(10, 10) { SRID = 4326 }, IdempotencyKey = Guid.NewGuid(), DeviceModel = "private"
    };

    /// <summary>Reads actual adapter response objects using the established coordinate converter.</summary>
    private static JsonElement HistoryJson(IActionResult response) => JsonSerializer.SerializeToElement(
        Assert.IsType<OkObjectResult>(response).Value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new PointJsonConverter() } });

    /// <summary>Extracts marker IDs without inventing ordering for unspecified SQL results.</summary>
    private static int[] HistoryIds(JsonElement payload) =>
        payload.GetProperty("results").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray();

    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new InMemoryActivityGeneratedKey())
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    private static GroupsController MakeController(ApplicationDbContext db, string userId)
    {
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(),
            new LocationService(db));
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task Latest_Returns_One_Per_User()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var u1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, u1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G", null);
        await gs.AddMemberAsync(g.Id, owner.Id, u1.Id, GroupMember.Roles.Member);

        // Seed previously admitted sensor observations; unknown historical rows are not current candidates.
        var p1 = new Point(10, 10) { SRID = 4326 };
        var p2 = new Point(11, 11) { SRID = 4326 };
        db.Locations.AddRange(
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-10),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-10), PresenceObservedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            },
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p2, Timestamp = DateTime.UtcNow.AddMinutes(-5),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-5), PresenceObservedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            },
            new Location
            {
                UserId = u1.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-8),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-8), PresenceObservedAtUtc = DateTime.UtcNow.AddMinutes(-8)
            }
        );
        await db.SaveChangesAsync();

        var ctrl = MakeController(db, owner.Id);
        var req = new GroupLocationsLatestRequest { IncludeUserIds = new List<string> { owner.Id, u1.Id } };
        var resp = await ctrl.Latest(g.Id, req, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        var list = Assert.IsAssignableFrom<IEnumerable<PublicLocationDto>>(ok.Value);
        Assert.Equal(2, list.Count());
    }

    /// <summary>Web and Mobile intersect explicit IDs identically for latest and history, including an opted-out caller.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task OrganizationAudienceAgreesAcrossControllers(bool enabled, bool sourceHidden, bool peerVisible)
    {
        using var db = MakeDb();
        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var peer = new ApplicationUser { Id = "peer", UserName = "peer", DisplayName = "Peer", IsActive = true };
        db.Users.AddRange(caller, peer);
        var group = new Group { Id = Guid.NewGuid(), Name = "Org", OwnerUserId = caller.Id, GroupType = "Organization", OrgPeerVisibilityEnabled = enabled };
        db.Groups.Add(group);
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = caller.Id, Role = GroupMember.Roles.Owner, OrgPeerVisibilityAccessDisabled = true },
            new GroupMember { GroupId = group.Id, UserId = peer.Id, OrgPeerVisibilityAccessDisabled = sourceHidden });
        var now = DateTime.UtcNow;
        foreach (var user in new[] { caller, peer })
            db.Locations.Add(new Location { UserId = user.Id, Coordinates = new Point(10, 10) { SRID = 4326 }, Timestamp = now, LocalTimestamp = now, PresenceObservedAtUtc = now, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();
        var web = MakeController(db, caller.Id);
        var mobile = MakeMobileController(db, caller.Id);
        var requested = new List<string> { caller.Id, peer.Id, "outside" };
        var expected = peerVisible ? new[] { "caller", "peer" } : new[] { "caller" };
        var latestRequest = new GroupLocationsLatestRequest { IncludeUserIds = requested };
        foreach (var response in new[] { await web.Latest(group.Id, latestRequest, default), await mobile.Latest(group.Id, latestRequest, default) })
        {
            var locations = Assert.IsAssignableFrom<IEnumerable<PublicLocationDto>>(Assert.IsType<OkObjectResult>(response).Value);
            Assert.Equal(expected, locations.Select(l => l.UserId).Order());
        }
        var query = new GroupLocationsQueryRequest { UserIds = requested, MinLng = -180, MinLat = -90, MaxLng = 180, MaxLat = 90,
            DateType = "day", Year = now.Year, Month = now.Month, Day = now.Day };
        foreach (var response in new[] { await web.Query(group.Id, query, default), await mobile.Query(group.Id, query, default) })
        {
            var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(response).Value,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new PointJsonConverter() } });
            Assert.Equal(expected.Length, json.GetProperty("totalItems").GetInt32());
            Assert.Equal(expected, json.GetProperty("results").EnumerateArray().Select(l => l.GetProperty("userId").GetString()).Order());
        }
    }

    /// <summary>An owner without active membership cannot obtain group locations or mutate policy.</summary>
    [Fact]
    public async Task FormerOwnerCannotReadOrManage()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "Owner", IsActive = true };
        db.Users.Add(owner);
        var group = new Group { Id = Guid.NewGuid(), Name = "Org", OwnerUserId = owner.Id, GroupType = "Organization" };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = owner.Id, Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Left });
        await db.SaveChangesAsync();
        var web = MakeController(db, owner.Id);
        var mobile = MakeMobileController(db, owner.Id);
        foreach (var response in new[]
        {
            await web.Members(group.Id, default),
            await web.Latest(group.Id, new GroupLocationsLatestRequest(), default),
            await mobile.Latest(group.Id, new GroupLocationsLatestRequest(), default),
            await web.Query(group.Id, new GroupLocationsQueryRequest(), default),
            await mobile.Query(group.Id, new GroupLocationsQueryRequest(), default),
            await web.ToggleOrgPeerVisibility(group.Id, new OrgPeerVisibilityToggleRequest { Enabled = true }, default),
        }) Assert.Equal(403, Assert.IsType<StatusCodeResult>(response).StatusCode);
        var summaries = Assert.IsAssignableFrom<IEnumerable<MobileGroupSummaryDto>>(Assert.IsType<OkObjectResult>(await mobile.Get("all", default)).Value);
        Assert.Empty(summaries);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsType<OkObjectResult>(await web.Get("managed", default)).Value));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsType<OkObjectResult>(await web.Get(null, default)).Value));
        Assert.False(group.OrgPeerVisibilityEnabled);
    }

    /// <summary>Existing Web and published Mobile mutations store personal source-sharing only where effective.</summary>
    [Theory]
    [InlineData("Family", false, false)]
    [InlineData("Friends", false, true)]
    [InlineData("Organization", false, false)]
    [InlineData("Organization", true, true)]
    public async Task PersonalSharingControlMatchesEffectivePolicy(string type, bool enabled, bool accepts)
    {
        using var db = MakeDb();
        var user = new ApplicationUser { Id = "user", UserName = "user", DisplayName = "User", IsActive = true };
        db.Users.Add(user);
        var group = new Group { Id = Guid.NewGuid(), Name = "Group", OwnerUserId = user.Id, GroupType = type, OrgPeerVisibilityEnabled = enabled };
        var member = new GroupMember { GroupId = group.Id, UserId = user.Id };
        db.Groups.Add(group);
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();
        var request = new OrgPeerVisibilityAccessRequest { Disabled = true };
        var web = MakeController(db, user.Id);
        var mobile = MakeMobileController(db, user.Id);
        var responses = new[] { await web.SetMemberOrgPeerVisibilityAccess(group.Id, user.Id, request, default),
            await mobile.SetPeerVisibility(group.Id, request, default) };
        foreach (var response in responses)
        {
            if (accepts) Assert.IsType<OkObjectResult>(response);
            else Assert.IsType<BadRequestObjectResult>(response);
        }
        Assert.Equal(accepts, member.OrgPeerVisibilityAccessDisabled);
        if (type == "Organization" && enabled)
        {
            var summaries = Assert.IsAssignableFrom<IEnumerable<MobileGroupSummaryDto>>(Assert.IsType<OkObjectResult>(await mobile.Get("all", default)).Value);
            Assert.True(Assert.Single(summaries).HasOrgPeerVisibilityAccess);
        }
    }

    /// <summary>Uses the published Mobile bearer-token contract and production timeline authority.</summary>
    private static MobileGroupsController MakeMobileController(ApplicationDbContext db, string userId)
    {
        var token = Guid.NewGuid().ToString();
        db.ApiTokens.Add(new ApiToken { Name = "mobile-test", TokenHash = ApiTokenService.HashToken(token), UserId = userId,
            User = db.Users.Find(userId)!, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<SseService>().BuildServiceProvider()
        };
        http.Request.Headers.Authorization = $"Bearer {token}";
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = http }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        return new MobileGroupsController(db, NullLogger<BaseApiController>.Instance, accessor, new UserColorService(),
            new GroupTimelineService(db, new LocationService(db), new ConfigurationBuilder().Build()))
            { ControllerContext = new ControllerContext { HttpContext = http } };
    }
}
