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
        var svc = new GroupService(db);
        var group = await svc.CreateGroupAsync("u1", "G1", null);
        await svc.JoinGroupAsync(group.Id, "u1", GroupMember.Roles.Member);

        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, svc, new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<Group>>(view.Model);
        Assert.Single(model);
    }
}
