using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

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
