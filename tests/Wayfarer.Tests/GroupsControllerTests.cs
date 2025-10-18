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
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests;

public class GroupsControllerTests
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
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(), new LocationService(db));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task Create_Returns_Created()
    {
        using var db = MakeDb();
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" });
        await db.SaveChangesAsync();
        var ctrl = MakeController(db, "u1");
        var resp = await ctrl.Create(new GroupCreateRequest { Name = "G1" }, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(resp);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task Leave_Updates_Status()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G2", null);

        var ctrl = MakeController(db, owner.Id);
        var resp = await ctrl.Leave(g.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupMembers.AnyAsync(m =>
            m.GroupId == g.Id && m.UserId == owner.Id && m.Status == GroupMember.MembershipStatuses.Left));
    }
}
