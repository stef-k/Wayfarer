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
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Integration;

public class GroupLocationsApiTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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

        // seed locations
        var p1 = new Point(10, 10) { SRID = 4326 };
        var p2 = new Point(11, 11) { SRID = 4326 };
        db.Locations.AddRange(
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-10),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-10)
            },
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p2, Timestamp = DateTime.UtcNow.AddMinutes(-5),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-5)
            },
            new Location
            {
                UserId = u1.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-8),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-8)
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
            db.Locations.Add(new Location { UserId = user.Id, Coordinates = new Point(10, 10) { SRID = 4326 }, Timestamp = now, LocalTimestamp = now, TimeZoneId = "UTC" });
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
