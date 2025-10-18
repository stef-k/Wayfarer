using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class OrgPeerVisibilityApiTests
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
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(), new Wayfarer.Parsers.LocationService(db));
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task Admin_Can_Toggle_Group_Visibility()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var m1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, m1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "Org Group", null);
        g.GroupType = "Organisation";
        await db.SaveChangesAsync();
        await gs.AddMemberAsync(g.Id, owner.Id, m1.Id, GroupMember.Roles.Member);

        var ctrl = MakeController(db, owner.Id);
        var resp = await ctrl.ToggleOrgPeerVisibility(g.Id, new OrgPeerVisibilityToggleRequest { Enabled = true }, default);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(db.Groups.First(x => x.Id == g.Id).OrgPeerVisibilityEnabled);
    }

    [Fact]
    public async Task Member_Cannot_Toggle_Group_Visibility()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var m1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, m1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "Org Group", null);
        g.GroupType = "Organisation";
        await db.SaveChangesAsync();
        await gs.AddMemberAsync(g.Id, owner.Id, m1.Id, GroupMember.Roles.Member);

        var ctrl = MakeController(db, m1.Id);
        var resp = await ctrl.ToggleOrgPeerVisibility(g.Id, new OrgPeerVisibilityToggleRequest { Enabled = true }, default);
        switch (resp)
        {
            case ObjectResult o:
                Assert.Equal(403, o.StatusCode);
                break;
            case StatusCodeResult sc:
                Assert.Equal(403, sc.StatusCode);
                break;
            case ForbidResult:
                // acceptable representation of 403
                break;
            default:
                Assert.True(false, $"Unexpected result type: {resp.GetType().Name}");
                break;
        }
    }

    [Fact]
    public async Task Self_Can_Set_Access_Disabled()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var m1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, m1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "Org Group", null);
        g.GroupType = "Organisation";
        await db.SaveChangesAsync();
        await gs.AddMemberAsync(g.Id, owner.Id, m1.Id, GroupMember.Roles.Member);

        var ctrl = MakeController(db, m1.Id);
        var resp = await ctrl.SetMemberOrgPeerVisibilityAccess(g.Id, m1.Id, new OrgPeerVisibilityAccessRequest { Disabled = true }, default);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(db.GroupMembers.First(x => x.GroupId == g.Id && x.UserId == m1.Id).OrgPeerVisibilityAccessDisabled);
    }
}
