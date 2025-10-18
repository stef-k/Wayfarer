using System.Security.Claims;
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
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests;

public class GroupLocationsApiTests
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
        var controller = new GroupsController(db, new GroupService(db), new NullLogger<GroupsController>(),
            new LocationService(db));
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task Latest_Returns_One_Per_User()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var u1 = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.AddRange(owner, u1);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G", null);
        await gs.AddMemberAsync(g.Id, owner.Id, u1.Id, GroupMember.Roles.Member);

        // seed locations
        var p1 = new Point(10, 10) { SRID = 4326 };
        var p2 = new Point(11, 11) { SRID = 4326 };
        db.Locations.AddRange(
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-10),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-10)
            },
            new Location
            {
                UserId = owner.Id, TimeZoneId = "UTC", Coordinates = p2, Timestamp = DateTime.UtcNow.AddMinutes(-5),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-5)
            },
            new Location
            {
                UserId = u1.Id, TimeZoneId = "UTC", Coordinates = p1, Timestamp = DateTime.UtcNow.AddMinutes(-8),
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-8)
            }
        );
        await db.SaveChangesAsync();

        var ctrl = MakeController(db, owner.Id);
        var req = new GroupLocationsLatestRequest { IncludeUserIds = new List<string> { owner.Id, u1.Id } };
        var resp = await ctrl.Latest(g.Id, req, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(resp);
        var list = Assert.IsAssignableFrom<IEnumerable<PublicLocationDto>>(ok.Value);
        Assert.Equal(2, list.Count());
    }
}