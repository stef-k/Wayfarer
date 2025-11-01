using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class MobileSseControllerTests
{
    private static (ApplicationDbContext Db, MobileSseController Controller, SseService Sse) CreateController(string? token = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        var sse = new SseService();
        var timeline = new GroupTimelineService(db, new LocationService(db));

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }

        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);
        var controller = new MobileSseController(db, NullLogger<BaseApiController>.Instance, accessor, sse, timeline)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (db, controller, sse);
    }

    [Fact]
    public async Task LocationUpdate_ReturnsUnauthorized_WhenNoToken()
    {
        var (_, controller, _) = CreateController();
        var result = await controller.SubscribeToUserAsync("any", CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task LocationUpdate_ReturnsForbidden_WhenNotPermitted()
    {
        var (db, controller, _) = CreateController("token");
        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        db.Users.Add(caller);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", UserId = caller.Id, User = caller, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        
        var result = await controller.SubscribeToUserAsync("other", CancellationToken.None);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task LocationUpdate_AllowsSelfAndStreams()
    {
        using var cts = new CancellationTokenSource(50);
        var (db, controller, _) = CreateController("token");
        var user = new ApplicationUser { Id = "user", UserName = "me", DisplayName = "Me", IsActive = true };
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var task = controller.SubscribeToUserAsync("me", cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var result = await task;
        Assert.IsType<EmptyResult>(result);
    }
}
