using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    [Fact]
    public async Task Create_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Create("Name", "Desc", "Family");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Create_ReturnsModelError_ForInvalidGroupType()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        var httpContext = BuildHttpContextWithUser(user.Id);
        var tempDataProvider = new InMemoryTempDataProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ITempDataProvider>(tempDataProvider);
        httpContext.RequestServices = services.BuildServiceProvider();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, tempDataProvider);
        controller.TempData["AlertMessage"] = string.Empty;
        controller.TempData["AlertType"] = "success";

        var result = await controller.Create("Name", "Desc", "Organization");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey("groupType"));
    }

    [Fact]
    public async Task Create_ReturnsView_WhenNameMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        var httpContext = BuildHttpContextWithUser(user.Id);
        var tempDataProvider = new InMemoryTempDataProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ITempDataProvider>(tempDataProvider);
        httpContext.RequestServices = services.BuildServiceProvider();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, tempDataProvider);

        var result = await controller.Create("", "Desc", "Family");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Members_ReturnsNotFound_WhenGroupMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Members(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Map_ReturnsForbid_WhenNotMember()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = "other", Name = "G1" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Map(group.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Map_ReturnsView_WhenMember()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "G1" };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new Wayfarer.Parsers.SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Map(group.Id);

        Assert.IsType<ViewResult>(result);
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _store = new Dictionary<string, object>();
        public IDictionary<string, object> LoadTempData(HttpContext context) => _store;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) => _store = new Dictionary<string, object>(values);
    }
}
