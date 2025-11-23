using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the GroupsController API endpoints.
/// </summary>
public class GroupsControllerTests : TestBase
{
    /// <summary>
    /// Creates a GroupsController configured with the specified user for authentication.
    /// </summary>
    /// <param name="db">The database context.</param>
    /// <param name="userId">The authenticated user ID.</param>
    /// <returns>A configured GroupsController instance.</returns>
    private GroupsController CreateController(ApplicationDbContext db, string userId)
    {
        var controller = new GroupsController(
            db,
            new GroupService(db),
            new NullLogger<GroupsController>(),
            new LocationService(db));
        return ConfigureControllerWithUser(controller, userId);
    }

    [Fact]
    public async Task Query_ReturnsUnauthorized_WhenUserMissing()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(), new LocationService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
        };

        var result = await controller.Query(Guid.NewGuid(), new GroupLocationsQueryRequest
        {
            MinLat = 0,
            MinLng = 0,
            MaxLat = 1,
            MaxLng = 1,
            ZoomLevel = 5
        }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Query_ReturnsForbidden_WhenNotMember()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var result = await controller.Query(Guid.NewGuid(), new GroupLocationsQueryRequest
        {
            MinLat = 0,
            MinLng = 0,
            MaxLat = 1,
            MaxLng = 1,
            ZoomLevel = 5
        }, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Query_ReturnsLocations_ForActiveMember()
    {
        var db = CreateDbContext();
        var current = TestDataFixtures.CreateUser(id: "u1");
        var friend = TestDataFixtures.CreateUser(id: "u2");
        db.Users.AddRange(current, friend);
        var group = new Group { Id = Guid.NewGuid(), Name = "Test Group", OwnerUserId = current.Id, GroupType = "Friends", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Groups.Add(group);
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = current.Id, Status = GroupMember.MembershipStatuses.Active, Role = GroupMember.Roles.Owner },
            new GroupMember { GroupId = group.Id, UserId = friend.Id, Status = GroupMember.MembershipStatuses.Active, Role = GroupMember.Roles.Member, OrgPeerVisibilityAccessDisabled = false });
        db.Locations.AddRange(
            new Location { Id = 1, UserId = current.Id, Coordinates = new Point(0.5, 0.5) { SRID = 4326 }, LocalTimestamp = DateTime.UtcNow, Timestamp = DateTime.UtcNow, TimeZoneId = "UTC" },
            new Location { Id = 2, UserId = friend.Id, Coordinates = new Point(0.6, 0.6) { SRID = 4326 }, LocalTimestamp = DateTime.UtcNow, Timestamp = DateTime.UtcNow, TimeZoneId = "UTC" }
        );
        await db.SaveChangesAsync();
        var controller = CreateController(db, current.Id);

        var result = await controller.Query(group.Id, new GroupLocationsQueryRequest
        {
            MinLat = 0,
            MinLng = 0,
            MaxLat = 1,
            MaxLng = 1,
            ZoomLevel = 5,
            DateType = "day",
            Year = DateTime.UtcNow.Year,
            Month = DateTime.UtcNow.Month,
            Day = DateTime.UtcNow.Day
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var totalItems = payload.GetType().GetProperty("totalItems")?.GetValue(payload) as int?;
        var results = payload.GetType().GetProperty("results")?.GetValue(payload) as IEnumerable<object>;
        Assert.Equal(2, totalItems);
        Assert.NotNull(results);
        Assert.Equal(2, results!.Count());
    }

    [Fact]
    public async Task ToggleOrgPeerVisibility_ReturnsUnauthorized_WhenMissingUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(), new LocationService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
        };

        var result = await controller.ToggleOrgPeerVisibility(Guid.NewGuid(), new OrgPeerVisibilityToggleRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task ToggleOrgPeerVisibility_ReturnsBadRequest_WhenNotOrganization()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var group = new Group { Id = Guid.NewGuid(), Name = "Test Group", OwnerUserId = user.Id, GroupType = "Friends", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = user.Id, Status = GroupMember.MembershipStatuses.Active, Role = GroupMember.Roles.Owner });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var result = await controller.ToggleOrgPeerVisibility(group.Id, new OrgPeerVisibilityToggleRequest { Enabled = true }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_Returns_Created()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var ctrl = CreateController(db, "u1");

        // Act
        var resp = await ctrl.Create(new GroupCreateRequest { Name = "G1" }, CancellationToken.None);

        // Assert
        var created = Assert.IsType<CreatedAtActionResult>(resp);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task Leave_Updates_Status()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G2", null);

        var ctrl = CreateController(db, owner.Id);

        // Act
        var resp = await ctrl.Leave(g.Id, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupMembers.AnyAsync(m =>
            m.GroupId == g.Id && m.UserId == owner.Id && m.Status == GroupMember.MembershipStatuses.Left));
    }
}
