using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User groups UI controller.
/// </summary>
public class UserGroupsControllerUiTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsGroups_ForCurrentUser()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "G1",
            OwnerUserId = "u1",
            GroupType = "Friends",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = "u1",
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var svc = new GroupService(db);

        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, svc, new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var joined = Assert.IsAssignableFrom<IEnumerable<object>>(view.ViewData["Joined"]);
        Assert.Single(joined);
    }
}
