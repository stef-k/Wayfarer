using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public class MobileGroupsControllerTests
{
    private static ApplicationDbContext MakeDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static HttpContext CreateContext(string? token = null)
    {
        var ctx = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(token)) ctx.Request.Headers["Authorization"] = $"Bearer {token}";
        return ctx;
    }

    private static MobileGroupsController MakeController(ApplicationDbContext db, string? token = null, IConfiguration? configuration = null)
    {
        var httpContext = CreateContext(token);
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        configuration ??= new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var timeline = new GroupTimelineService(db, new LocationService(db), configuration);
        var color = new UserColorService();
        var controller =
            new MobileGroupsController(db, NullLogger<BaseApiController>.Instance, accessor, color, timeline)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        return controller;
    }

    [Fact]
    public async Task Get_ReturnsJoinedGroups()
    {
        using var db = MakeDb();
        var user = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Friends",
            GroupType = "Friends",
            OwnerUserId = "owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken
            { Name = "mobile", Token = "token", User = user, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        });
        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5 });
        await db.SaveChangesAsync();

        var controller = MakeController(db, "token");
        var result = await controller.Get("joined", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<MobileGroupSummaryDto>>(ok.Value);
        Assert.Single(payload);
    }

    [Fact]
    public async Task Members_ReturnsForbidden_WhenNotMember()
    {
        using var db = MakeDb();
        var user = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var group = new Group
        {
            Id = Guid.NewGuid(), Name = "Group", OwnerUserId = "owner", CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken
            { Name = "mobile", Token = "token", User = user, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = MakeController(db, "token");
        var result = await controller.Members(group.Id, CancellationToken.None);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }
}
