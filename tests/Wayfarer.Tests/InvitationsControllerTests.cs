using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class InvitationsControllerTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    private static InvitationsController MakeController(ApplicationDbContext db, string userId)
    {
        var controller = new InvitationsController(db, new InvitationService(db), new NullLogger<InvitationsController>());
        var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task List_Returns_Pending()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o" };
        var user = new ApplicationUser { Id = "u", UserName = "u" };
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();
        var gs = new GroupService(db);
        var isvc = new InvitationService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G", null);
        var inv = await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        var ctrl = MakeController(db, user.Id);
        var resp = await ctrl.ListForCurrentUser(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Accept_By_Id_Works()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o" };
        var user = new ApplicationUser { Id = "u", UserName = "u" };
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();
        var gs = new GroupService(db);
        var isvc = new InvitationService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G2", null);
        var inv = await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        var ctrl = MakeController(db, user.Id);
        var resp = await ctrl.Accept(inv.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == user.Id));
    }
}

