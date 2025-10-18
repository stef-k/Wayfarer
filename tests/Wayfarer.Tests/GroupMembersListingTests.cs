using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class GroupMembersListingTests
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
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>());
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task Members_Forbidden_For_NonMember()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var outsider = new ApplicationUser { Id = "x", UserName = "x", DisplayName = "x" };
        db.Users.AddRange(owner, outsider);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G", null);

        var ctrl = MakeController(db, outsider.Id);
        var resp = await ctrl.Members(g.Id, CancellationToken.None);
        var s = Assert.IsType<ObjectResult>(resp);
        Assert.Equal(403, s.StatusCode);
    }

    [Fact]
    public async Task Members_Returns_Roster_For_Member()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var m1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, m1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G2", null);
        await gs.AddMemberAsync(g.Id, owner.Id, m1.Id, GroupMember.Roles.Member);

        var ctrl = MakeController(db, owner.Id);
        var resp = await ctrl.Members(g.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.NotNull(ok.Value);
    }
}

