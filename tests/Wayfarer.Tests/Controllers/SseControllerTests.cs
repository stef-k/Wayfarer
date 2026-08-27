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
        ClaimsPrincipal? user = null,
        SseService? sse = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var service = sse ?? new SseService();
        var options = new MobileSseOptions();
        var controller = new SseController(
            service,
            db,
            timelineService,
            options,
            scopeFactory ?? new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
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

    [Theory]
    [InlineData(false, "now")]
    [InlineData(true, null)]
    [InlineData(true, "1d")]
    [InlineData(true, "stale")]
    public async Task Stream_LocationUpdateRejectsNonLiveOrInvalidPublicTimeline(bool isPublic, string? threshold)
    {
        using var db = CreateDb();
        var user = new ApplicationUser
        {
            Id = "user-1",
            UserName = "alice",
            DisplayName = "Alice",
            IsTimelinePublic = isPublic,
            PublicTimelineTimeThreshold = threshold
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>());

        await controller.Stream("location-update", "alice", CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, controller.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(false, "now")]
    [InlineData(true, "1d")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "stale")]
    [InlineData(true, "1z")]
    public async Task Stream_LocationUpdateWithholdsLaterEvent_WhenLiveEligibilityIsPersistentlyRevokedInAnotherContext(bool isPublic, string? threshold)
    {
        var databaseName = Guid.NewGuid().ToString();
        using var services = new ServiceCollection()
            .AddEntityFrameworkInMemoryDatabase()
            .AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .UseInternalServiceProvider(services)
            .Options;
        using var subscriptionDb = new ApplicationDbContext(dbOptions, services);
        using var settingsDb = new ApplicationDbContext(dbOptions, services);
        var user = new ApplicationUser
        {
            Id = "user-1",
            UserName = "alice",
            DisplayName = "Alice",
            IsTimelinePublic = true,
            PublicTimelineTimeThreshold = "now"
        };
        subscriptionDb.Users.Add(user);
        await subscriptionDb.SaveChangesAsync();
        var sse = new SseService();
        var controller = CreateController(
            subscriptionDb,
            Mock.Of<IGroupTimelineService>(),
            sse: sse,
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        using var cts = new CancellationTokenSource();
        var streamTask = controller.Stream("location-update", "alice", cts.Token);
        await Task.Delay(25);
        var persistedUser = await settingsDb.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persistedUser.IsTimelinePublic = isPublic;
        persistedUser.PublicTimelineTimeThreshold = threshold;
        await settingsDb.SaveChangesAsync();

        Assert.True(user.IsTimelinePublic);
        Assert.Equal("now", user.PublicTimelineTimeThreshold);

        await sse.BroadcastAsync("location-update-alice", "{\"location\":true}");
        cts.Cancel();
        await streamTask;

        Assert.Empty(((MemoryStream)controller.HttpContext.Response.Body).ToArray());
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

    [Fact]
    public async Task ImportStreamWithoutAuthenticatedIdentityIsUnauthorized()
    {
        using var db = CreateDb();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>());

        var result = await controller.SubscribeToImportAsync(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact(Timeout = 10_000)]
    public async Task ImportStreamDeliversOnlySameUserContentFreeEvent()
    {
        using var db = CreateDb();
        var sse = new SseService();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>(), CreateUser("owner"), sse);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscription = controller.SubscribeToImportAsync(cts.Token);
        while (!controller.Response.Headers.ContainsKey("Content-Type"))
            await Task.Yield();

        await sse.BroadcastAsync("import-other", "{\"type\":\"import-state\"}");
        Assert.Empty(((MemoryStream)controller.Response.Body).ToArray());
        await sse.BroadcastAsync("import-owner", "{\"type\":\"enrichment-state\",\"address\":\"private\"}");
        Assert.Empty(((MemoryStream)controller.Response.Body).ToArray());
        await sse.BroadcastAsync("import-owner", "{\"type\":\"import-state\"}");
        cts.Cancel();
        await subscription;

        var payload = System.Text.Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());
        Assert.Contains("{\"type\":\"import-state\"}", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("other", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Missing claim identity cannot own a protected notification channel.</summary>
    [Fact]
    public async Task GroupNotificationStreamWithoutAuthenticatedIdentityIsUnauthorized()
    {
        using var db = CreateDb();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>());

        var result = await controller.SubscribeToGroupNotificationsAsync(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>The protected stream derives its channel and accepts only exact reload hints.</summary>
    [Fact(Timeout = 10_000)]
    public async Task GroupNotificationStreamDeliversOnlySameUserExactContentFreeEvents()
    {
        using var db = CreateDb();
        var sse = new SseService();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>(), CreateUser("owner"), sse);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscription = controller.SubscribeToGroupNotificationsAsync(cts.Token);
        while (!controller.Response.Headers.ContainsKey("Content-Type"))
            await Task.Yield();

        await sse.BroadcastAsync("group-notifications-other", "{\"type\":\"invitation-state\"}");
        await sse.BroadcastAsync("group-notifications-owner", "{\"type\":\"invitation-state\",\"groupId\":\"private\"}");
        await sse.BroadcastAsync("group-notifications-owner", "{\"type\":\"unrelated\"}");
        Assert.Empty(((MemoryStream)controller.Response.Body).ToArray());
        await sse.BroadcastAsync("group-notifications-owner", "{\"type\":\"invitation-state\"}");
        await sse.BroadcastAsync("group-notifications-owner", "{\"type\":\"membership-state\"}");
        cts.Cancel();
        await subscription;

        var payload = System.Text.Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());
        Assert.Contains("{\"type\":\"invitation-state\"}", payload, StringComparison.Ordinal);
        Assert.Contains("{\"type\":\"membership-state\"}", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("other", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unrelated", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyCallerSelectedImportChannelIsRejected()
    {
        using var db = CreateDb();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>(), CreateUser("owner"));

        await controller.Stream("import", "other-user", CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("import-other")]
    [InlineData("enrichment")]
    [InlineData("enrichment-other")]
    [InlineData("invitation-update")]
    [InlineData("Invitation-Update-other")]
    [InlineData("membership-update")]
    [InlineData("MEMBERSHIP-UPDATE-other")]
    public async Task LegacyGenericRouteRejectsEverySensitiveChannelPrefix(string type)
    {
        using var db = CreateDb();
        var controller = CreateController(db, Mock.Of<IGroupTimelineService>());

        await controller.Stream(type, "foreign-user", CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
    }
}
