using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;
using LocationEntity = Wayfarer.Models.Location;

namespace Wayfarer.Tests;

public class MobileGroupLocationsControllerTests
{
    private static ApplicationDbContext MakeDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static HttpContext CreateContext(string? token = null)
    {
        var ctx = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(token))
        {
            ctx.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        return ctx;
    }

    private static MobileGroupsController MakeController(ApplicationDbContext db, string? token = null)
    {
        var httpContext = CreateContext(token);
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);
        var timeline = new GroupTimelineService(db, new LocationService(db));
        var color = new UserColorService();
        var controller = new MobileGroupsController(db, NullLogger<BaseApiController>.Instance, accessor, color, timeline)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller;
    }

    private static Point MakePoint(double lat, double lng) => new(lng, lat) { SRID = 4326 };

    [Fact]
    public async Task MobileGroupLocations_Latest_ReturnsResults_ForMember()
    {
        using var db = MakeDb();
        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var other = new ApplicationUser { Id = "other", UserName = "other", DisplayName = "Other", IsActive = true };
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Org",
            GroupType = "Organization",
            OwnerUserId = caller.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.AddRange(caller, other);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", User = caller, UserId = caller.Id, CreatedAt = DateTime.UtcNow });
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = caller.Id, Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
            new GroupMember { GroupId = group.Id, UserId = other.Id, Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow });
        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5 });
        db.Locations.AddRange(
            new LocationEntity { UserId = caller.Id, Coordinates = MakePoint(10, 10), Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" },
            new LocationEntity { UserId = other.Id, Coordinates = MakePoint(11, 11), Timestamp = DateTime.UtcNow.AddMinutes(-2), LocalTimestamp = DateTime.UtcNow.AddMinutes(-2), TimeZoneId = "UTC" });
        await db.SaveChangesAsync();

        var controller = MakeController(db, "token");
        var response = await controller.Latest(group.Id, new GroupLocationsLatestRequest(), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response);
        var list = Assert.IsAssignableFrom<IEnumerable<PublicLocationDto>>(ok.Value);
        Assert.Equal(2, list.Count());
    }

    [Fact]
    public async Task MobileGroupLocations_Latest_ReturnsForbidden_ForNonMember()
    {
        using var db = MakeDb();
        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var group = new Group { Id = Guid.NewGuid(), Name = "Org", OwnerUserId = "owner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(caller);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", User = caller, UserId = caller.Id, CreatedAt = DateTime.UtcNow });
        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5 });
        await db.SaveChangesAsync();

        var controller = MakeController(db, "token");
        var response = await controller.Latest(group.Id, new GroupLocationsLatestRequest(), CancellationToken.None);
        var status = Assert.IsType<StatusCodeResult>(response);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task MobileGroupLocations_Query_RestrictsToAllowedMembers()
    {
        using var db = MakeDb();
        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var other = new ApplicationUser { Id = "other", UserName = "other", DisplayName = "Other", IsActive = true };
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Friends",
            GroupType = "Friends",
            OwnerUserId = caller.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.AddRange(caller, other);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", User = caller, UserId = caller.Id, CreatedAt = DateTime.UtcNow });
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = caller.Id, Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
            new GroupMember { GroupId = group.Id, UserId = other.Id, Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true, JoinedAt = DateTime.UtcNow });
        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5 });
        db.Locations.Add(new LocationEntity { UserId = caller.Id, Coordinates = MakePoint(10, 10), Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();

        var controller = MakeController(db, "token");
        var request = new GroupLocationsQueryRequest
        {
            MinLng = -180,
            MinLat = -90,
            MaxLng = 180,
            MaxLat = 90,
            ZoomLevel = 10
        };

        var response = await controller.Query(group.Id, request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response);
        var payload = ok.Value!;
        var payloadType = payload.GetType();
        var total = (int)payloadType.GetProperty("totalItems")!.GetValue(payload)!;
        Assert.Equal(1, total);
    }
}
