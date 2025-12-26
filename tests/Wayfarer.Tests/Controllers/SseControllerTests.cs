using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API SSE stream controller coverage.
/// </summary>
public class SseControllerTests
{
    private static ApplicationDbContext CreateDb()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(dbOptions, new ServiceCollection().BuildServiceProvider());
    }

    private static SseController CreateController(
        ApplicationDbContext db,
        IGroupTimelineService timelineService,
        ClaimsPrincipal? user = null)
    {
        var service = new SseService();
        var options = new MobileSseOptions();
        var controller = new SseController(service, db, timelineService, options);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (user != null)
        {
            context.User = user;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static ClaimsPrincipal CreateUser(string userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static GroupTimelineAccessContext CreateAccessContext(bool isMember)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            OwnerUserId = "owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var callerMembership = isMember ? new GroupMember { UserId = "user-123" } : null;
        var activeMembers = new List<GroupMember>();
        var allowedUserIds = new HashSet<string>();

        return (GroupTimelineAccessContext)Activator.CreateInstance(
            typeof(GroupTimelineAccessContext),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { group, callerMembership, activeMembers, allowedUserIds, false, 30 },
            culture: null)!;
    }

    [Fact]
    public async Task Stream_SetsEventStreamHeaders_AndCompletesOnCancellation()
    {
        using var db = CreateDb();
        var mockTimelineService = new Mock<IGroupTimelineService>();
        var controller = CreateController(db, mockTimelineService.Object);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await controller.Stream("trip", "abc", cts.Token);

        Assert.Equal("text/event-stream", controller.HttpContext.Response.Headers["Content-Type"].ToString());
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task SubscribeToGroupAsync_WithoutUser_ReturnsUnauthorized()
    {
        using var db = CreateDb();
        var mockTimelineService = new Mock<IGroupTimelineService>();
        var controller = CreateController(db, mockTimelineService.Object, user: null);
        var groupId = Guid.NewGuid();

        var result = await controller.SubscribeToGroupAsync(groupId, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SubscribeToGroupAsync_GroupNotFound_ReturnsNotFound()
    {
        using var db = CreateDb();
        var mockTimelineService = new Mock<IGroupTimelineService>();
        mockTimelineService
            .Setup(s => s.BuildAccessContextAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupTimelineAccessContext?)null);

        var user = CreateUser("user-123");
        var controller = CreateController(db, mockTimelineService.Object, user);
        var groupId = Guid.NewGuid();

        var result = await controller.SubscribeToGroupAsync(groupId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task SubscribeToGroupAsync_NonMember_ReturnsForbid()
    {
        using var db = CreateDb();
        var mockTimelineService = new Mock<IGroupTimelineService>();
        var nonMemberContext = CreateAccessContext(isMember: false);
        mockTimelineService
            .Setup(s => s.BuildAccessContextAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(nonMemberContext);

        var user = CreateUser("user-123");
        var controller = CreateController(db, mockTimelineService.Object, user);
        var groupId = Guid.NewGuid();

        var result = await controller.SubscribeToGroupAsync(groupId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SubscribeToGroupAsync_Member_SubscribesSuccessfully()
    {
        using var db = CreateDb();
        var mockTimelineService = new Mock<IGroupTimelineService>();
        var memberContext = CreateAccessContext(isMember: true);
        mockTimelineService
            .Setup(s => s.BuildAccessContextAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memberContext);

        var user = CreateUser("user-123");
        var controller = CreateController(db, mockTimelineService.Object, user);
        var groupId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        var result = await controller.SubscribeToGroupAsync(groupId, cts.Token);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("text/event-stream", controller.HttpContext.Response.Headers["Content-Type"].ToString());
    }
}
