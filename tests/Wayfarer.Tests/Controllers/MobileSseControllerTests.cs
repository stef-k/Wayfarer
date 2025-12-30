using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the MobileSseController which handles Server-Sent Events for mobile clients.
/// </summary>
public class MobileSseControllerTests : TestBase
{
    /// <summary>
    /// Creates a MobileSseController with test configuration.
    /// </summary>
    /// <param name="token">Optional bearer token for authentication.</param>
    /// <returns>A tuple containing the database context, controller, and SSE service.</returns>
    private (ApplicationDbContext Db, MobileSseController Controller, SseService Sse) CreateController(string? token = null)
    {
        var db = CreateDbContext();
        var sse = new SseService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var timeline = new GroupTimelineService(db, new LocationService(db), configuration);
        var sseOptions = new MobileSseOptions { HeartbeatIntervalMilliseconds = 50 };

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }

        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        var controller = new MobileSseController(db, NullLogger<BaseApiController>.Instance, accessor, sse, timeline, sseOptions)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (db, controller, sse);
    }

    [Fact]
    public async Task GroupStream_ReturnsUnauthorized_WhenNoToken()
    {
        // Arrange
        var (_, controller, _) = CreateController();

        // Act
        var result = await controller.SubscribeToGroupAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GroupStream_ReturnsNotFound_WhenGroupDoesNotExist()
    {
        // Arrange
        var (db, controller, _) = CreateController("token");
        var caller = TestDataFixtures.CreateUser(id: "caller");
        db.Users.Add(caller);

        var token = TestDataFixtures.CreateApiToken(caller, "token");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        // Act
        var result = await controller.SubscribeToGroupAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GroupStream_ReturnsForbidden_WhenNotMember()
    {
        // Arrange
        var (db, controller, _) = CreateController("token");
        var caller = TestDataFixtures.CreateUser(id: "caller");
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.AddRange(caller, owner);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Private Group",
            GroupType = "Friends",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        });

        var token = TestDataFixtures.CreateApiToken(caller, "token");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        // Act
        var result = await controller.SubscribeToGroupAsync(group.Id, CancellationToken.None);

        // Assert
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task GroupStream_AllowsMemberAndStreams()
    {
        // Arrange
        var (db, controller, _) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user", username: "me");
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "My Group",
            GroupType = "Friends",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        });

        var token = TestDataFixtures.CreateApiToken(user, "token");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        // Use a longer timeout to allow database operations to complete before cancellation
        using var cts = new CancellationTokenSource(500);

        // Act
        var task = controller.SubscribeToGroupAsync(group.Id, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var result = await task;

        // Assert
        Assert.IsType<EmptyResult>(result);
    }

    #region Visit SSE Endpoint Tests

    [Fact]
    public async Task VisitsStream_ReturnsUnauthorized_WhenNoToken()
    {
        // Arrange
        var (_, controller, _) = CreateController();

        // Act
        var result = await controller.SubscribeToVisitsAsync(CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task VisitsStream_AllowsAuthenticatedUser()
    {
        // Arrange
        var (db, controller, _) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user", username: "visitor");
        db.Users.Add(user);

        var token = TestDataFixtures.CreateApiToken(user, "token");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        // Use a timeout to allow subscription to start before cancellation
        using var cts = new CancellationTokenSource(500);

        // Act
        var task = controller.SubscribeToVisitsAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var result = await task;

        // Assert
        Assert.IsType<EmptyResult>(result);
    }

    #endregion
}
